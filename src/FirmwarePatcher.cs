using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;

// Generates a custom Maxwell firmware (v1.0.1.56, .61, .63, or .74; Xbox or
// PlayStation) with patched L/R balance defaults baked in.
//
// Per-version structure (verified May 2026):
//
//   v56  — single-profile baseline.  ONE balance NVDM key (0xF668), one
//          default value (factory-shipped 142/142, symmetric).  NO F665,
//          NO F702, NO per-source switching at all.  The L/R imbalance
//          bug doesn't exist in v56 because the asymmetric F665 default
//          was added in v61.
//
//   v61+ — per-source switching system added (incomplete: the master
//          event router that would drive the dispatch table at 0x081C3134
//          is missing, so the system never actually runs at runtime).
//          TWO balance NVDM keys (F665 = USB-C asymmetric 141/149,
//          F668 = wireless symmetric 147/147), selected by NVDM 0xF702.
//          F702 is never updated by the firmware itself; it's a frozen
//          factory value.  Units that shipped with F702 = 0x0A load the
//          asymmetric F665 and exhibit the L/R imbalance.
//
// The custom firmware patches per-version:
//
//   v56:  rewrites the single F668 default to the user's calibrated L/R.
//
//   v61+: rewrites BOTH F665 and F668 defaults to the same calibrated L/R
//         (so balance is correct regardless of which profile loads), AND
//         patches the F702 reader function (FUN_0x0817B2F4) to always
//         return 0 - pinning the firmware to the wireless audio profile
//         regardless of NVDM 0xF702.
//
// All patch sites are located by pattern search, so it is variant- and
// version-independent: it works on Xbox and PS variants without hard-coded
// offsets.
//
// NOTE: the patched balance takes effect after a FACTORY RESET of the
// headset (a flash updates the code; a factory reset runs the default
// registration that writes NVDM). Confirmed empirically.
static class FirmwarePatcher
{
    const int HEADER = 0x1000;

    // Versions we can build custom firmware for. v74 is the recommended
    // default for users who want all bug fixes; v56 is the "pre-feature"
    // baseline for users who want the simplest possible firmware (no
    // per-source machinery at all).
    public static readonly string[] SupportedVersions = { "56", "61", "63", "74" };

    static ushort Rd16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    static uint Rd32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    // Load the embedded stock firmware base for the given platform and version.
    // version is one of "56", "61", "63", "74".
    public static byte[] LoadStockBase(bool playstation, string version)
    {
        if (Array.IndexOf(SupportedVersions, version) < 0)
            throw new ArgumentException($"Unsupported firmware version '{version}'. Supported: {string.Join(", ", SupportedVersions)}.");
        string want = playstation ? $"stock_ps_{version}.bin" : $"stock_xbox_{version}.bin";
        var asm = Assembly.GetExecutingAssembly();
        foreach (var n in asm.GetManifestResourceNames())
        {
            if (n.EndsWith(want, StringComparison.OrdinalIgnoreCase))
            {
                using var s = asm.GetManifestResourceStream(n);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        throw new Exception($"Embedded stock firmware '{want}' not found in the executable.");
    }

    // Build a custom firmware (any supported version; Xbox or PS) with the
    // given balance L/R baked into the NVDM balance default(s).
    public static byte[] Build(bool playstation, string version, int balL, int balR)
    {
        if (balL < 0 || balL > 150 || balR < 0 || balR > 150)
            throw new ArgumentException("Balance values must be 0-150 (driver-safety cap).");
        if (Array.IndexOf(SupportedVersions, version) < 0)
            throw new ArgumentException($"Unsupported firmware version '{version}'.");

        byte[] raw = LoadStockBase(playstation, version);
        byte[] header = raw[0..HEADER];
        byte[] payload = raw[HEADER..];

        int[] parts = PartitionSizes(header);
        long decSize = parts.Sum(x => (long)x);
        int dictSize = (int)Rd32(payload, 1);   // LZMA dictionary size from the original props

        byte[] decomp = LzmaDecompress(payload[0..5], payload[13..], decSize);

        int imm = ((balR & 0xFF) << 8) | (balL & 0xFF);
        byte[] movw = EncodeMovw(3, imm);

        // F668 (wireless/dongle balance) exists in every supported version.
        int btOff = FindBalanceMovw(decomp, 0xF668);
        Array.Copy(movw, 0, decomp, btOff, 4);

        // F665 (USB-C balance) and the F702 reader were added in v61.
        // v56 has neither; the patches are skipped for v56.
        if (version != "56")
        {
            int usbOff = FindBalanceMovw(decomp, 0xF665);
            Array.Copy(movw, 0, decomp, usbOff, 4);

            // Force the NVDM 0xF702 reader (FUN_0x0817B2F4) to always return 0,
            // pinning every F702-keyed decision to the wireless audio profile.
            PatchSourceReader(decomp);
        }

        // Recompute per-partition SHA-256 (TLV 0x0014).
        int t14 = FindTlv(header, 0x0014);
        uint hcount = Rd32(header, t14 + 4);
        int foff = 0;
        for (int i = 0; i < hcount && i < parts.Length; i++)
        {
            byte[] h = SHA256.HashData(decomp.AsSpan(foff, parts[i]));
            Array.Copy(h, 0, header, t14 + 8 + i * 32, 32);
            foff += parts[i];
        }

        // Recompress and assemble the LZMA-alone payload (5 props + 8 size + stream).
        var (props, stream) = LzmaCompress(decomp, dictSize);
        byte[] alone = new byte[5 + 8 + stream.Length];
        Array.Copy(props, 0, alone, 0, 5);
        for (int i = 0; i < 8; i++) alone[5 + i] = (byte)((decSize >> (8 * i)) & 0xFF);
        Array.Copy(stream, 0, alone, 13, stream.Length);

        // Update TLV 0x0011 LZMA stream size.
        int t11 = FindTlv(header, 0x0011);
        int so = t11 + 4 + 6;
        uint sz = (uint)alone.Length;
        header[so] = (byte)sz;
        header[so + 1] = (byte)(sz >> 8);
        header[so + 2] = (byte)(sz >> 16);
        header[so + 3] = (byte)(sz >> 24);

        // Assemble + outer SHA-256 over file[0x100:].
        byte[] outRaw = new byte[HEADER + alone.Length];
        Array.Copy(header, outRaw, HEADER);
        Array.Copy(alone, 0, outRaw, HEADER, alone.Length);
        byte[] outer = SHA256.HashData(outRaw.AsSpan(0x100));
        Array.Copy(outer, 0, outRaw, 0, 32);
        return outRaw;
    }

    // Patch the F702-reader wrapper (FUN_0x0817B2F4) to always return 0.
    // Original 4-byte prologue:
    //     07 B5        push {r0, r1, r2, lr}
    //     FF 23        movs r3, #0xFF
    // Patched to:
    //     00 20        movs r0, #0
    //     70 47        bx lr
    // After patching, the function returns 0 immediately; every caller (boot
    // DSP init, RACE balance writer, state handler) sees "wireless profile".
    //
    // The patch site is located by pattern search using a 20-byte unique
    // signature that includes the F702 key load. Verified unique in both
    // stock_xbox_74 and stock_ps_74 (May 2026).
    static void PatchSourceReader(byte[] fw)
    {
        // 20-byte signature: prologue + key load.
        //   07 B5         push {r0,r1,r2,lr}
        //   FF 23         movs r3, #0xFF
        //   8D F8 03 30   strb.w r3, [sp, #3]
        //   01 AA         add r2, sp, #4
        //   01 23         movs r3, #1
        //   0D F1 03 01   add.w r1, sp, #3
        //   4F F2 02 70   movw r0, #0xF702
        byte[] sig = {
            0x07, 0xB5, 0xFF, 0x23, 0x8D, 0xF8, 0x03, 0x30,
            0x01, 0xAA, 0x01, 0x23, 0x0D, 0xF1, 0x03, 0x01,
            0x4F, 0xF2, 0x02, 0x70,
        };
        int o = IndexOf(fw, sig, 0);
        if (o < 0)
            throw new Exception("F702 reader patch site not found - firmware version mismatch.");
        // Make sure there's only one match (so we don't patch the wrong site).
        if (IndexOf(fw, sig, o + 1) >= 0)
            throw new Exception("F702 reader patch site is ambiguous - multiple matches.");
        // movs r0, #0 ; bx lr
        fw[o + 0] = 0x00;
        fw[o + 1] = 0x20;
        fw[o + 2] = 0x70;
        fw[o + 3] = 0x47;
    }

    static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        int n = needle.Length;
        for (int i = start; i + n <= hay.Length; i++)
        {
            int j = 0;
            while (j < n && hay[i + j] == needle[j]) j++;
            if (j == n) return i;
        }
        return -1;
    }

    // Locate the 'movw r3,#<default>' that feeds an NVDM key's default
    // registration. The site is a 'movw r0,#<key>' preceded exactly 8 bytes
    // earlier by a 'movw r3,#imm' — unique to the default-registration routine.
    static int FindBalanceMovw(byte[] fw, int key)
    {
        byte[] keyMovw = EncodeMovw(0, key);
        var span = (ReadOnlySpan<byte>)fw;
        int o = 0;
        while (true)
        {
            int rel = span.Slice(o).IndexOf(keyMovw);
            if (rel < 0) break;
            int pos = o + rel;
            var v = DecodeMovw(fw, pos - 8);
            if (v.HasValue && v.Value.rd == 3) return pos - 8;
            o = pos + 2;
        }
        throw new Exception($"Balance-default site for NVDM key 0x{key:X4} not found.");
    }

    static (int rd, int imm16)? DecodeMovw(byte[] b, int o)
    {
        if (o < 0 || o + 4 > b.Length) return null;
        ushort hw1 = Rd16(b, o), hw2 = Rd16(b, o + 2);
        if ((hw1 & 0xFBF0) != 0xF240) return null;     // not a Thumb-2 movw
        if ((hw2 & 0x8000) != 0) return null;
        int imm4 = hw1 & 0xF, i = (hw1 >> 10) & 1;
        int imm3 = (hw2 >> 12) & 7, rd = (hw2 >> 8) & 0xF, imm8 = hw2 & 0xFF;
        return (rd, (imm4 << 12) | (i << 11) | (imm3 << 8) | imm8);
    }

    static byte[] EncodeMovw(int rd, int imm16)
    {
        int imm4 = (imm16 >> 12) & 0xF, i = (imm16 >> 11) & 1;
        int imm3 = (imm16 >> 8) & 7, imm8 = imm16 & 0xFF;
        int hw1 = 0xF000 | (i << 10) | 0x0240 | imm4;
        int hw2 = (imm3 << 12) | ((rd & 0xF) << 8) | imm8;
        return new byte[] { (byte)hw1, (byte)(hw1 >> 8), (byte)hw2, (byte)(hw2 >> 8) };
    }

    static int[] PartitionSizes(byte[] h)
    {
        int pt = 0x12e;
        if (Rd16(h, pt) != 0x0012)
            throw new Exception("Partition-table TLV (0x0012) not found — wrong firmware base.");
        uint count = Rd32(h, pt + 4);
        var s = new int[count];
        for (int i = 0; i < count; i++) s[i] = (int)Rd32(h, pt + 8 + i * 12 + 4);
        return s;
    }

    static int FindTlv(byte[] h, ushort tag)
    {
        int o = 0x100;
        while (o < 0x1000)
        {
            if (h[o] == 0xFF) { o++; continue; }
            if (o + 4 > 0x1000) break;
            if (Rd16(h, o) == tag) return o;
            o += 4 + Rd16(h, o + 2);
        }
        throw new Exception($"TLV 0x{tag:X4} not found in firmware header.");
    }

    static byte[] LzmaDecompress(byte[] props, byte[] stream, long outSize)
    {
        using var inMs = new MemoryStream(stream);
        using var lz = new LzmaStream(props, inMs, stream.Length, outSize);
        byte[] o = new byte[outSize];
        int off = 0;
        while (off < outSize)
        {
            int n = lz.Read(o, off, (int)(outSize - off));
            if (n <= 0) break;
            off += n;
        }
        if (off != outSize) throw new Exception("LZMA decompression incomplete.");
        return o;
    }

    static (byte[] props, byte[] stream) LzmaCompress(byte[] data, int dictSize)
    {
        using var outMs = new MemoryStream();
        var ep = new LzmaEncoderProperties(true, dictSize);  // eos marker, original dict
        byte[] props;
        using (var enc = new LzmaStream(ep, false, outMs))
        {
            props = enc.Properties;
            enc.Write(data, 0, data.Length);
        }
        return (props, outMs.ToArray());
    }
}
