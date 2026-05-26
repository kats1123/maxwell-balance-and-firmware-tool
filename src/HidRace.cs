using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Raw-HID access to the Maxwell for RACE balance + firmware-version reads.
// Pure Win32 P/Invoke — no external dependency.
//
// RACE framing reused from the proven set_lr.py / read_now.py:
//   write balance : output report 0x06, body 80 05 5A 05 00 00 09 <sub> 00 <val>
//                   sub 0x29 = Left, 0x2A = Right; applies live + persists to NVDM.
//   read memory   : cmd 0x1680 — 4-byte read of any 0x14xxxxxx SRAM or
//                   0x08xxxxxx flash address (works over the USB-C cable).
static class HidRace
{
    const ushort VID = 0x3329;
    static readonly ushort[] PIDS = { 0x4B1A, 0x4B1E, 0x4B18, 0x4B19 };
    const ushort USAGE_PAGE = 0xFF13;

    // The firmware-version string ("v1.0.1.NN\0") lives in flash partition 1.
    // These flash addresses were derived by decompressing the actual stock
    // .bin files and locating the string per variant per version. RACE 0x1680
    // can read flash, so we read the string straight off the running firmware.
    static readonly uint[] XBOX_VER_ADDRS = { 0x0828EA20, 0x0828E990 }; // .74, then .63/.61
    static readonly uint[] PS_VER_ADDRS = { 0x0828E5C8, 0x0828E538 };   // .74, then .63
    // Dongle version string lives at a fixed flash address (stable .61/.63/.74).
    const uint XBOX_DONGLE_VER_ADDR = 0x081EEDD8;
    const uint PS_DONGLE_VER_ADDR = 0x081E78F8;

    public class Result
    {
        public bool Ok;
        public string Message = "";
        public int L, R;
    }

    public class VersionResult
    {
        public bool Connected;     // a Maxwell HID interface was opened
        public bool Found;         // a version string was resolved
        public ushort Pid;
        public string Version = "";
    }

    // Result of an audio source-state (NVDM 0xF702) read.
    public class SourceResult
    {
        public bool Ok;
        public string Message = "";
        public int State = -1;     // raw 0xF702 byte; 0x0A = USB-C, else dongle/wireless
    }

    // What Maxwell devices are physically connected right now.
    public class ConnInfo
    {
        public bool DongleConnected;      // a wireless-dongle PID is present
        public bool HeadsetUsbConnected;  // a USB-C headset PID is present
        public bool IsPs;                 // first device is a PlayStation variant
        public ushort FirstPid;
        public bool Any => DongleConnected || HeadsetUsbConnected;
    }

    // ---------- Win32 ----------
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    static readonly IntPtr INVALID = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid Guid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
    [DllImport("hid.dll")] static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr en, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr dev, IntPtr di, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr dev, ref SP_DEVICE_INTERFACE_DATA d,
        IntPtr detail, int detailSize, out int reqSize, IntPtr di);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr dev);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    static IEnumerable<string> EnumerateHidPaths()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID) yield break;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(buf, 8);   // cbSize of detail struct (8 on 64-bit Unicode)
                    if (SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, out _, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(buf + 4);
                        if (!string.IsNullOrEmpty(path)) yield return path;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Find + open the Maxwell RACE HID interface. Caller must CloseHandle.
    static IntPtr Open(out int outLen, out int inLen, out ushort pid)
    {
        outLen = inLen = 0; pid = 0;
        foreach (string path in EnumerateHidPaths())
        {
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                                  IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID) continue;
            bool keep = false;
            try
            {
                var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (HidD_GetAttributes(h, ref attr) && attr.VendorID == VID && Array.IndexOf(PIDS, attr.ProductID) >= 0)
                {
                    if (HidD_GetPreparsedData(h, out IntPtr pre))
                    {
                        var caps = new HIDP_CAPS();
                        HidP_GetCaps(pre, ref caps);
                        HidD_FreePreparsedData(pre);
                        // The RACE interface is the col02 collection; on some
                        // units (notably the dongle) its usage page reads back
                        // differently, so accept the col02 path as a fallback.
                        bool col02 = path.IndexOf("col02", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (caps.UsagePage == USAGE_PAGE || col02)
                        {
                            outLen = caps.OutputReportByteLength > 0 ? caps.OutputReportByteLength : 63;
                            inLen = caps.InputReportByteLength > 0 ? caps.InputReportByteLength : 63;
                            pid = attr.ProductID;
                            keep = true;
                        }
                    }
                }
            }
            catch { }
            if (keep) return h;
            CloseHandle(h);
        }
        return INVALID;
    }

    static byte[] BuildOutReport(int outLen, byte[] body)
    {
        byte[] rep = new byte[outLen];
        rep[0] = 0x06;                          // report ID
        rep[1] = (byte)(body.Length & 0xFF);    // body length, LE u16
        rep[2] = (byte)(body.Length >> 8);
        Array.Copy(body, 0, rep, 3, Math.Min(body.Length, outLen - 3));
        return rep;
    }

    // 1-byte length framing for the 0x09xx command family. The device parses
    // the RACE packet at a fixed offset, so the 0x05 0x5A magic must land at
    // byte 3: 06 <len> 80 05 5A ...  (the 0x1680 body itself starts with
    // 05 5A, so that one uses the 2-byte BuildOutReport instead).
    static byte[] BuildReport1(int outLen, byte[] body)
    {
        byte[] rep = new byte[outLen];
        rep[0] = 0x06;
        rep[1] = (byte)body.Length;
        Array.Copy(body, 0, rep, 2, Math.Min(body.Length, outLen - 2));
        return rep;
    }

    // Returns "Xbox", "PlayStation", or null if no headset is connected.
    public static string DetectVariant()
    {
        IntPtr h = Open(out _, out _, out ushort pid);
        if (h == INVALID) return null;
        CloseHandle(h);
        return (pid == 0x4B1A || pid == 0x4B19) ? "PlayStation" : "Xbox";
    }

    // Enumerate every connected Maxwell device and classify it. Matches on
    // VID+PID only (a query-access handle), so it detects the dongle and the
    // headset regardless of how their HID collections enumerate.
    public static ConnInfo ProbeConnection()
    {
        var ci = new ConnInfo();
        foreach (string path in EnumerateHidPaths())
        {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID) continue;
            try
            {
                var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (HidD_GetAttributes(h, ref attr) && attr.VendorID == VID
                    && Array.IndexOf(PIDS, attr.ProductID) >= 0)
                {
                    ushort pid = attr.ProductID;
                    if (ci.FirstPid == 0) { ci.FirstPid = pid; ci.IsPs = pid == 0x4B19 || pid == 0x4B1A; }
                    if (pid == 0x4B18 || pid == 0x4B19) ci.DongleConnected = true;
                    if (pid == 0x4B1A || pid == 0x4B1E) ci.HeadsetUsbConnected = true;
                }
            }
            catch { }
            CloseHandle(h);
        }
        return ci;
    }

    // ---------- RACE 0x1680 memory read ----------

    // Read 4 bytes from `addr` (cmd 0x1680). Returns null on no response.
    static byte[] ReadWord(IntPtr h, int outLen, int inLen, uint addr, int attempts, int windowMs)
    {
        byte[] body =
        {
            0x05, 0x5A, 0x08, 0x00, 0x80, 0x16, 0x00, 0x00,
            (byte)(addr & 0xFF), (byte)((addr >> 8) & 0xFF),
            (byte)((addr >> 16) & 0xFF), (byte)((addr >> 24) & 0xFF),
        };
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            WriteFile(h, BuildOutReport(outLen, body), outLen, out _, IntPtr.Zero);
            var collected = new List<byte>();
            long deadline = Environment.TickCount64 + windowMs;
            while (Environment.TickCount64 < deadline)
            {
                byte[] inBuf = new byte[inLen];
                inBuf[0] = 0x07;
                if (HidD_GetInputReport(h, inBuf, inLen))
                {
                    int ln = inBuf[1] | (inBuf[2] << 8);
                    if (ln > 0 && 3 + ln <= inLen)
                        for (int k = 0; k < ln; k++) collected.Add(inBuf[3 + k]);
                }
                Thread.Sleep(8);
            }
            if (TryExtractWord(collected, addr, out byte[] w)) return w;
            Thread.Sleep(60);
        }
        return null;
    }

    // Scan a response stream for a cmd-0x1680 reply echoing `addr`:
    //   05 5b .. .. 80 16 .. .. <addr:4> <data:4>
    static bool TryExtractWord(List<byte> buf, uint addr, out byte[] word)
    {
        word = null;
        byte[] a = { (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16), (byte)(addr >> 24) };
        for (int i = 0; i + 8 <= buf.Count; i++)
        {
            if (buf[i] != a[0] || buf[i + 1] != a[1] || buf[i + 2] != a[2] || buf[i + 3] != a[3]) continue;
            bool marker = false;
            for (int j = Math.Max(0, i - 16); j < i; j++)
                if (buf[j] == 0x05 && j + 1 < i && buf[j + 1] == 0x5B) { marker = true; break; }
            if (!marker) continue;
            word = new[] { buf[i + 4], buf[i + 5], buf[i + 6], buf[i + 7] };
            return true;
        }
        return false;
    }

    // ---------- balance ----------

    // Read current L/R balance via RACE cmd 0x0901 sub 0x29/0x2A (gain read).
    // This command relays through the dongle, so it works over the USB-C cable
    // AND the wireless dongle.
    public static Result ReadBalance()
    {
        var r = new Result();
        IntPtr h = Open(out int outLen, out int inLen, out _);
        if (h == INVALID) { r.Message = "Headset not found. Connect it via USB-C or the dongle."; return r; }
        try
        {
            // Read L/R pairs until two consecutive reads agree. A stale packet
            // (e.g. right after a write) reads as a wildly-off value; the real
            // balance is stable, so a self-consistent pair is the true value.
            int prevL = -1, prevR = -1;
            for (int round = 0; round < 6; round++)
            {
                int l = ReadGain(h, outLen, inLen, 0x29, 4);
                int rr = l >= 0 ? ReadGain(h, outLen, inLen, 0x2A, 4) : -1;
                if (l < 0 || rr < 0) break;
                if (l == prevL && rr == prevR)
                {
                    r.Ok = true; r.L = l; r.R = rr;
                    r.Message = $"Current balance: L={l}  R={rr}";
                    return r;
                }
                prevL = l; prevR = rr;
                Thread.Sleep(120);
            }
            if (prevL >= 0 && prevR >= 0)
            {
                r.Ok = true; r.L = prevL; r.R = prevR;
                r.Message = $"Current balance: L={prevL}  R={prevR}";
                return r;
            }
            r.Message = "No response from the headset.";
            return r;
        }
        finally { CloseHandle(h); }
    }

    // Discard pending input reports, so a read doesn't pick up a stale packet
    // (e.g. the response left over from a preceding balance write).
    static void DrainInput(IntPtr h, int inLen)
    {
        for (int k = 0; k < 16; k++)
        {
            byte[] b = new byte[inLen];
            b[0] = 0x07;
            if (!HidD_GetInputReport(h, b, inLen)) break;
            if ((b[1] | (b[2] << 8)) == 0) break;
        }
    }

    // RACE cmd 0x0901 sub <sub> — read one gain channel. Returns 0-255, or -1.
    static int ReadGain(IntPtr h, int outLen, int inLen, byte sub, int attempts)
    {
        byte[] body = { 0x80, 0x05, 0x5A, 0x04, 0x00, 0x01, 0x09, sub };
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            DrainInput(h, inLen);
            WriteFile(h, BuildReport1(outLen, body), outLen, out _, IntPtr.Zero);
            long deadline = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < deadline)
            {
                byte[] inBuf = new byte[inLen];
                inBuf[0] = 0x07;
                if (HidD_GetInputReport(h, inBuf, inLen))
                {
                    int ln = inBuf[1] | (inBuf[2] << 8);
                    if (ln > 0)
                    {
                        int max = Math.Min(3 + ln, inLen);
                        for (int b = 3; b + 9 < max; b++)
                            if (inBuf[b] == 0x05 && inBuf[b + 1] == 0x5B && inBuf[b + 6] == sub)
                                return inBuf[b + 9];
                    }
                }
                Thread.Sleep(10);
            }
            Thread.Sleep(50);
        }
        return -1;
    }

    // Read the audio source state (NVDM 0xF702) via RACE cmd 0x0901 sub 0x2F.
    // State 0x0A = USB-C profile; any other value = dongle / wireless profile.
    // 0x0901 relays through the dongle, so this works over USB-C or the dongle.
    public static SourceResult ReadSourceState()
    {
        var r = new SourceResult();
        IntPtr h = Open(out int outLen, out int inLen, out _);
        if (h == INVALID) { r.Message = "Headset not found. Connect it via USB-C or the dongle."; return r; }
        try
        {
            int st = ReadGain(h, outLen, inLen, 0x2F, 6);
            if (st < 0) { r.Message = "No response from the headset."; return r; }
            r.Ok = true;
            r.State = st;
            return r;
        }
        finally { CloseHandle(h); }
    }

    // Write the audio source state (NVDM 0xF702) via RACE cmd 0x0900 sub 0x2F.
    // The Maxwell firmware has the per-source profile system designed but the
    // master event router is removed - so once F702 is written, nothing in
    // the firmware ever changes it. The write persists for the life of the
    // headset (or until another host re-writes it). 0x00 = wireless profile,
    // which is the correct one for this product.
    public static SourceResult WriteSourceState(byte value)
    {
        var r = new SourceResult();
        IntPtr h = Open(out int outLen, out int inLen, out _);
        if (h == INVALID) { r.Message = "Headset not found. Connect it via USB-C or the dongle."; return r; }
        try
        {
            // RACE cmd 0x0900 sub 0x2F: 1-byte payload = new F702 value.
            byte[] body = { 0x80, 0x05, 0x5A, 0x05, 0x00, 0x00, 0x09, 0x2F, 0x00, value };
            for (int attempt = 0; attempt < 4; attempt++)
            {
                DrainInput(h, inLen);
                WriteFile(h, BuildReport1(outLen, body), outLen, out _, IntPtr.Zero);
                Thread.Sleep(150);
                // Verify by reading back.
                int got = ReadGain(h, outLen, inLen, 0x2F, 4);
                if (got == value)
                {
                    r.Ok = true;
                    r.State = got;
                    return r;
                }
                Thread.Sleep(100);
            }
            r.Message = "Write did not stick - try again.";
            return r;
        }
        finally { CloseHandle(h); }
    }

    // Write L/R balance via RACE (cmd 0x0900 sub 0x29 = L, 0x2A = R).
    // Applies live to the DSP and persists to NVDM.
    public static Result WriteBalance(int L, int R)
    {
        var r = new Result { L = L, R = R };
        if (L < 0 || L > 150 || R < 0 || R > 150)
        {
            r.Message = "Balance values must be between 0 and 150.";
            return r;
        }
        if (L == 0x88 || L == 0x8E || R == 0x88 || R == 0x8E)
        {
            r.Message = "136 and 142 are reserved firmware codes - pick a nearby value instead.";
            return r;
        }
        IntPtr h = Open(out int outLen, out int inLen, out _);
        if (h == INVALID) { r.Message = "Headset not found. Connect it and try again."; return r; }
        try
        {
            SendBalance(h, outLen, 0x29, L);
            Thread.Sleep(150);
            SendBalance(h, outLen, 0x2A, R);
            Thread.Sleep(150);
            r.Ok = true;
            r.Message = $"Applied L={L}  R={R} — live now and saved to the headset.";
            return r;
        }
        finally { CloseHandle(h); }
    }

    static void SendBalance(IntPtr h, int outLen, byte sub, int val)
    {
        byte[] body = { 0x80, 0x05, 0x5A, 0x05, 0x00, 0x00, 0x09, sub, 0x00, (byte)val };
        WriteFile(h, BuildReport1(outLen, body), outLen, out _, IntPtr.Zero);
    }

    // ---------- firmware version ----------

    // Read the running firmware version straight off flash (cmd 0x1680).
    // For a cabled headset this is the headset's version; over the dongle
    // link cmd 0x1680 hits the dongle's own flash, i.e. the dongle's version.
    public static VersionResult ReadFirmwareVersion()
    {
        var vr = new VersionResult();
        IntPtr h = Open(out int outLen, out int inLen, out ushort pid);
        if (h == INVALID) return vr;
        vr.Connected = true;
        vr.Pid = pid;
        try
        {
            uint[] addrs = pid switch
            {
                0x4B18 => new[] { XBOX_DONGLE_VER_ADDR },   // Xbox dongle
                0x4B19 => new[] { PS_DONGLE_VER_ADDR },     // PlayStation dongle
                0x4B1A => PS_VER_ADDRS,                     // PlayStation headset (USB-C)
                _ => XBOX_VER_ADDRS,                        // Xbox headset (USB-C)
            };
            foreach (uint baseAddr in addrs)
            {
                var bytes = new List<byte>();
                bool ok = true;
                for (uint off = 0; off < 16; off += 4)
                {
                    byte[] w = ReadWord(h, outLen, inLen, baseAddr + off, attempts: 5, windowMs: 350);
                    if (w == null) { ok = false; break; }
                    bytes.AddRange(w);
                }
                if (!ok) continue;
                string v = ParseVersion(bytes);
                if (v != null) { vr.Found = true; vr.Version = v; return vr; }
            }
            return vr;
        }
        finally { CloseHandle(h); }
    }

    // Pull a "v1.0.1.74"-style string out of a flash byte run.
    static string ParseVersion(List<byte> b)
    {
        var sb = new StringBuilder();
        foreach (byte c in b)
        {
            if (c == 0) break;
            if (c < 0x20 || c > 0x7E) return null;
            sb.Append((char)c);
        }
        var m = Regex.Match(sb.ToString(), @"(\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
