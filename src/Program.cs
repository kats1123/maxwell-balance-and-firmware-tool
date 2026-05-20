using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;

class AirohaSDK
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UpdateCallback(int status, int msgId, IntPtr extraData);

    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int initializeAirohaSDK(ushort vid, ushort pid);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void closeAirohaSDK();
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void setTargetDevice(byte deviceType);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void registerUpdateResultCallback(UpdateCallback cb);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void requestDFUInfo();
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void setDfuMode(int mode);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void setBatteryLevel(int level);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void setDfuAgentFilepath([MarshalAs(UnmanagedType.LPStr)] string path);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void setPingTimerFlag(int flag);
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void startDataTransfer();
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void applyNewFirmware(int batteryLevel);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);

    public static void SetupDllPath()
    {
        SetDllDirectory(AppContext.BaseDirectory);
    }
}

class FlasherForm : Form
{
    ComboBox _platform, _target, _version;
    Button _flashBtn;
    TextBox _log;
    ProgressBar _progress;
    Label _status;
    AirohaSDK.UpdateCallback _callback = null!;
    volatile bool _readyToApply, _done, _flashing, _applying, _rebooting;

    static readonly ushort[] PIDS = [0x4B18, 0x4B19, 0x4B1A, 0x4B1E];

    public FlasherForm()
    {
        Text = "Audeze Maxwell Firmware Flasher";
        Size = new Size(500, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(30, 30, 34);
        ForeColor = Color.FromArgb(220, 220, 225);
        Font = new Font("Segoe UI", 10);

        var lbl1 = new Label { Text = "Platform:", Location = new Point(20, 20), AutoSize = true };
        _platform = new ComboBox { Location = new Point(120, 17), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        _platform.Items.AddRange(["Xbox", "PlayStation"]);
        _platform.SelectedIndex = 0;

        var lbl2 = new Label { Text = "Target:", Location = new Point(20, 55), AutoSize = true };
        _target = new ComboBox { Location = new Point(120, 52), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        _target.Items.AddRange(["Dongle", "Headset"]);
        _target.SelectedIndex = 0;

        var lbl3 = new Label { Text = "Version:", Location = new Point(20, 90), AutoSize = true };
        _version = new ComboBox { Location = new Point(120, 87), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        FindFirmwareVersions();

        _flashBtn = new Button
        {
            Text = "Flash Firmware",
            Location = new Point(340, 17),
            Size = new Size(130, 75),
            BackColor = Color.FromArgb(50, 130, 200),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 11),
        };
        _flashBtn.Click += OnFlash;

        _status = new Label
        {
            Text = "Ready. Select options and click Flash.",
            Location = new Point(20, 125),
            Size = new Size(440, 20),
            ForeColor = Color.FromArgb(160, 160, 165),
        };

        _progress = new ProgressBar
        {
            Location = new Point(20, 150),
            Size = new Size(440, 10),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
        };

        _log = new TextBox
        {
            Location = new Point(20, 170),
            Size = new Size(440, 250),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(20, 20, 24),
            ForeColor = Color.FromArgb(180, 180, 185),
            Font = new Font("Consolas", 9),
        };

        Controls.AddRange([lbl1, _platform, lbl2, _target, lbl3, _version, _flashBtn, _status, _progress, _log]);
    }

    void FindFirmwareVersions()
    {
        var fwDir = Path.Combine(AppContext.BaseDirectory, "firmware");
        if (!Directory.Exists(fwDir))
        {
            _version.Items.Add("v1.0.1.63");
            _version.SelectedIndex = 0;
            return;
        }

        var versions = Directory.GetFiles(fwDir, "*.bin")
            .Select(f => Path.GetFileName(f))
            .Where(f => f.Contains("v1.0.1."))
            .Select(f => { var i = f.IndexOf("v1.0.1."); var e = f.IndexOf('_', i + 7); return e > 0 ? f[i..e] : ""; })
            .Where(v => v.Length > 0)
            .Distinct()
            .OrderByDescending(v => v)
            .ToArray();

        foreach (var v in versions) _version.Items.Add(v);
        if (_version.Items.Count > 0) _version.SelectedIndex = 0;
    }

    string? FindFirmwareFile()
    {
        var plat = _platform.SelectedIndex == 0 ? "XBOX" : "PS4";
        var tgt = _target.SelectedIndex == 0 ? "dongle" : "headset";
        var ver = _version.Text;
        var fwDir = Path.Combine(AppContext.BaseDirectory, "firmware");
        var pattern = $"*{ver}*{plat}*{tgt}*";
        if (Directory.Exists(fwDir))
        {
            var files = Directory.GetFiles(fwDir, pattern);
            if (files.Length > 0) return files[0];
        }
        return null;
    }

    void Log(string msg)
    {
        if (InvokeRequired) { Invoke(() => Log(msg)); return; }
        _log.AppendText(msg + Environment.NewLine);
    }

    void SetStatus(string msg)
    {
        if (InvokeRequired) { Invoke(() => SetStatus(msg)); return; }
        _status.Text = msg;
    }

    async void OnFlash(object? sender, EventArgs e)
    {
        if (_flashing) return;

        var fwFile = FindFirmwareFile();
        if (fwFile == null)
        {
            MessageBox.Show("Firmware file not found.\nCheck the firmware/ folder.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var tgt = _target.SelectedIndex == 0 ? "dongle" : "headset";
        var msg = tgt == "dongle"
            ? "For DONGLE update:\n- USB dongle plugged in (PC mode)\n- Headset powered ON wirelessly\n- Audeze apps closed"
            : "For HEADSET update:\n- UNPLUG the USB dongle (must not be connected to PC)\n- Headset connected to the PC via USB-C cable\n- Confirm the headset is DETECTED in the Audeze app — that means\n  it is ready to flash (powered on or off does not matter)\n- Close the Audeze app before clicking OK";

        if (MessageBox.Show($"Flash {Path.GetFileName(fwFile)}?\n\n{msg}\n\nDO NOT disconnect during the process!",
            "Confirm Flash", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        _flashing = true;
        _applying = false;
        _rebooting = false;
        _flashBtn.Enabled = false;
        _progress.MarqueeAnimationSpeed = 30;
        _log.Clear();

        await Task.Run(() => DoFlash(fwFile));

        _progress.MarqueeAnimationSpeed = 0;
        _flashBtn.Enabled = true;
        _flashing = false;
    }

    void DoFlash(string fwFile)
    {
        _readyToApply = false;
        _done = false;
        _applying = false;
        _rebooting = false;
        _callback = OnSDKCallback;

        Log($"Firmware: {Path.GetFileName(fwFile)}");
        SetStatus("Connecting to device...");

        int result = -1;
        foreach (var pid in PIDS)
        {
            result = AirohaSDK.initializeAirohaSDK(0x3329, pid);
            if (result == 1)
            {
                string name = pid switch { 0x4B18 => "Xbox Dongle", 0x4B19 => "PS Dongle", 0x4B1A => "PS Headset (USB-C)", 0x4B1E => "Xbox Headset (USB-C)", _ => $"0x{pid:X4}" };
                Log($"Connected ({name})");
                break;
            }
            try { AirohaSDK.closeAirohaSDK(); } catch { }
            Thread.Sleep(500);
        }

        if (result != 1)
        {
            Log("FAILED to connect to device.");
            SetStatus("Connection failed. Check device and try again.");
            return;
        }

        try
        {
            AirohaSDK.setTargetDevice(0);
            AirohaSDK.registerUpdateResultCallback(_callback);

            SetStatus("Preparing firmware transfer...");
            Log("Requesting DFU info...");
            AirohaSDK.requestDFUInfo();
            Thread.Sleep(5000);

            AirohaSDK.setDfuMode(0);
            AirohaSDK.setBatteryLevel(20);
            AirohaSDK.setDfuAgentFilepath(Path.GetFullPath(fwFile));
            AirohaSDK.setPingTimerFlag(0);

            SetStatus("Transferring firmware — DO NOT DISCONNECT!");
            Log("Transfer started.");
            Log("This WILL take 3-5 minutes. Do not panic or close this window.");
            Log("If no progress after 10 minutes, close and retry.");
            Log("");
            AirohaSDK.startDataTransfer();

            for (int i = 0; i < 600; i++)
            {
                Thread.Sleep(1000);

                if (_readyToApply)
                {
                    _applying = true;
                    SetStatus("Applying firmware — device is rebooting...");
                    Log("Applying firmware update...");
                    AirohaSDK.applyNewFirmware(20);
                    _readyToApply = false;
                }

                // Once we see REBOOTING (status 7), the SDK has handed off — the chip
                // is now rebooting on its own. Stop polling/logging "Applying..." spam,
                // treat as success.
                if (_rebooting)
                {
                    SetStatus("Firmware update complete — device rebooting!");
                    Log("");
                    Log("*** SUCCESS! Firmware applied and device is rebooting. ***");
                    Log("Wait 30 seconds for the device to fully reboot.");
                    Invoke(() => MessageBox.Show(
                        "Firmware update complete!\n\nDevice is rebooting. Wait 30 seconds, then verify the new firmware version in the Audeze app.",
                        "Success", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    break;
                }

                if (_done)
                {
                    SetStatus("Firmware update complete!");
                    Log("");
                    Log("*** SUCCESS! Firmware updated. ***");
                    Log("Wait 30 seconds for the device to reboot.");
                    Invoke(() => MessageBox.Show(
                        "Firmware update complete!\n\nWait 30 seconds for the device to reboot, then check the Audeze app to verify the version.",
                        "Success", MessageBoxButtons.OK, MessageBoxIcon.Information));
                    break;
                }

                if (i > 0 && i % 30 == 0)
                {
                    if (_applying)
                        Log($"  Applying... ({i}s)");
                    else
                        Log($"  Transferring... ({i}s)");
                }
            }

            if (!_done)
            {
                SetStatus("Timed out. Close and retry.");
                Log("");
                Log("Timed out waiting for completion.");
                Log("Close this window, unplug/replug the device, and try again.");
            }

            Thread.Sleep(3000);
            AirohaSDK.closeAirohaSDK();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            SetStatus("Error occurred. Close and retry.");
        }
    }

    void OnSDKCallback(int status, int msgId, IntPtr extra)
    {
        if (status == 5) { _readyToApply = true; Log("  [READY_TO_APPLY]"); }
        else if (status == 1) { _done = true; Log("  [DFU_SUCCESS]"); }
        else if (status == 2) Log("  [FAIL/TIMEOUT]");
        else if (status == 6) { _applying = true; Log("  [APPLYING...]"); }
        else if (status == 7) { _rebooting = true; Log("  [REBOOTING...]"); }
    }
}

class Program
{
    [STAThread]
    static void Main()
    {
        AirohaSDK.SetupDllPath();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new FlasherForm());
    }
}
