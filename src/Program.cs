using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// ---- Airoha SDK P/Invoke (firmware flashing) ----
static class AirohaSDK
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
    // Factory reset. Disassembly of AirohaHidCoreLib.dll shows SetFactoryResetEx
    // takes a single 16-bit device selector (0 = the connected headset) and is
    // the same call the Audeze app uses for "factory reset".
    [DllImport("AirohaHidCoreLib.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SetFactoryResetEx(ushort deviceId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetDllDirectory(string path);

    public static void SetupDllPath() => SetDllDirectory(AppContext.BaseDirectory);
}

// Reads firmware versions through the Airoha SDK. setTargetDevice(0) selects the
// directly-connected device (the dongle, or a cabled headset); setTargetDevice(1)
// selects the partner — the headset reached wirelessly behind the dongle.
// requestDFUInfo() then reports that device's version on the update callback
// (msgId 0x2713, extraData = an ASCII version string). Confirmed against
// hardware: over the dongle, target 0 = dongle fw, target 1 = headset fw.
static class SdkVersions
{
    static readonly object _lock = new();
    static volatile string _ver;
    static AirohaSDK.UpdateCallback _cb;
    static readonly ushort[] PIDS = { 0x4B18, 0x4B19, 0x4B1A, 0x4B1E };

    static void OnCb(int status, int msgId, IntPtr extra)
    {
        if (msgId != 0x2713 || extra == IntPtr.Zero) return;
        try
        {
            string s = Marshal.PtrToStringAnsi(extra);
            if (!string.IsNullOrEmpty(s))
            {
                var m = Regex.Match(s, @"\d+\.\d+\.\d+\.\d+");
                if (m.Success) _ver = m.Value;
            }
        }
        catch { }
    }

    // dongleMode true: also query the partner (the headset behind the dongle).
    // Returns (primary, partner): primary = the connected device's version,
    // partner = the headset behind the dongle (null when not in dongle mode).
    public static (string primary, string partner) Read(bool dongleMode)
    {
        lock (_lock)
        {
            _cb = OnCb;
            int rc = -1;
            foreach (var pid in PIDS)
            {
                rc = AirohaSDK.initializeAirohaSDK(0x3329, pid);
                if (rc == 1) break;
                try { AirohaSDK.closeAirohaSDK(); } catch { }
                Thread.Sleep(250);
            }
            if (rc != 1) return (null, null);
            try
            {
                AirohaSDK.registerUpdateResultCallback(_cb);
                Thread.Sleep(250);
                string primary = QueryTarget(0);
                string partner = dongleMode ? QueryTarget(1) : null;
                return (primary, partner);
            }
            finally { try { AirohaSDK.closeAirohaSDK(); } catch { } }
        }
    }

    static string QueryTarget(byte target)
    {
        _ver = null;
        AirohaSDK.setTargetDevice(target);
        Thread.Sleep(700);
        AirohaSDK.requestDFUInfo();
        for (int i = 0; i < 170 && _ver == null; i++)
            Thread.Sleep(100);
        return _ver;
    }
}

class MaxwellForm : Form
{
    // ---- theme ----
    static readonly Color BG = Color.FromArgb(26, 27, 32);
    static readonly Color PANEL = Color.FromArgb(37, 38, 45);
    static readonly Color FG = Color.FromArgb(230, 230, 234);
    static readonly Color MUTED = Color.FromArgb(146, 147, 156);
    static readonly Color ACCENT = Color.FromArgb(64, 140, 224);
    static readonly Color GREEN = Color.FromArgb(72, 168, 112);
    static readonly Color FIELD = Color.FromArgb(19, 20, 24);
    static readonly Color WARN = Color.FromArgb(224, 168, 72);
    static readonly Color RED = Color.FromArgb(220, 96, 96);

    Panel _pageFw, _pageBal;
    Button _navFw, _navBal;

    // header — headset + dongle firmware lines
    Label _hdrHeadset, _hdrDongle;
    Button _hdrRefresh;

    // firmware page
    ComboBox _target, _version;
    Label _platformLabel;
    Button _flashBtn, _resetBtn, _removeBtn;
    Label _fwWarn, _fwStatus;
    ProgressBar _fwProgress;
    TextBox _fwLog;

    // balance page
    NumericUpDown _balL, _balR;
    Label _balCurrent;
    Button _readBtn, _applyBtn, _makeFwBtn, _resetBtn2, _srcBtn;
    TextBox _balLog;
    string _lastFwVersion = "74";  // last picked Make-Custom-Firmware base version

    // detected device state (filled by RefreshHeadsetInfo)
    bool _isPs;                   // detected platform: true = PlayStation
    string _headsetVer;           // headset firmware version, or null
    string _dongleVer;            // dongle firmware version, or null
    volatile bool _hdrBusy;       // a header refresh is in progress
    volatile bool _hdrPending;    // a refresh was requested while one was running
    System.Windows.Forms.Timer _pollTimer;
    bool _balAutoReadDone;        // balance auto-read on first Balance-tab open
    long _lastApplyTick;          // Environment.TickCount64 of the last Apply
    int _sourceStateCache = -1;   // NVDM 0xF702 cached in-memory. -1 = not read yet; 0x0A = USB-C; other = wireless. Cleared on app restart only.

    readonly Dictionary<string, string> _customFw = new();
    readonly List<FileSystemWatcher> _watchers = new();
    AirohaSDK.UpdateCallback _callback;
    volatile bool _readyToApply, _done, _flashing, _applying, _rebooting, _resetting, _failed;
    static readonly ushort[] PIDS = { 0x4B18, 0x4B19, 0x4B1A, 0x4B1E };

    static string FwDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    static string CustomDir => Path.Combine(AppContext.BaseDirectory, "custom");

    static string PidName(ushort pid) => pid switch
    {
        0x4B18 => "Xbox dongle",
        0x4B19 => "PlayStation dongle",
        0x4B1A => "PlayStation headset (USB-C cable)",
        0x4B1E => "Xbox headset (USB-C cable)",
        _ => $"device 0x{pid:X4}",
    };

    public MaxwellForm()
    {
        Text = "Maxwell Tool  v2.0";
        ClientSize = new Size(560, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BG;
        ForeColor = FG;
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "MAXWELL TOOL",
            Location = new Point(24, 18),
            AutoSize = true,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = FG,
        };
        var sub = new Label
        {
            Text = "Firmware flashing  -  L/R balance correction",
            Location = new Point(26, 50),
            AutoSize = true,
            ForeColor = MUTED,
        };
        Controls.Add(title);
        Controls.Add(sub);

        _hdrHeadset = new Label
        {
            Text = "Checking...",
            Location = new Point(340, 14),
            Size = new Size(196, 20),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = MUTED,
            Font = new Font("Segoe UI Semibold", 10.5f),
        };
        _hdrDongle = new Label
        {
            Text = "",
            Visible = false,
            Location = new Point(340, 35),
            Size = new Size(196, 20),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = MUTED,
            Font = new Font("Segoe UI Semibold", 10.5f),
        };
        _hdrRefresh = new Button
        {
            Text = "Recheck",
            Location = new Point(446, 63),
            Size = new Size(90, 22),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8f),
            ForeColor = MUTED,
            BackColor = PANEL,
            Cursor = Cursors.Hand,
        };
        _hdrRefresh.FlatAppearance.BorderSize = 0;
        _hdrRefresh.Click += (s, e) => RefreshHeadsetInfo();

        var helpBtn = new Button
        {
            Text = "Guide",
            Location = new Point(244, 21),
            Size = new Size(74, 25),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = FG,
            BackColor = PANEL,
            Cursor = Cursors.Hand,
        };
        helpBtn.FlatAppearance.BorderSize = 0;
        helpBtn.Click += (s, e) => ShowGuide();

        Controls.Add(_hdrHeadset);
        Controls.Add(_hdrDongle);
        Controls.Add(helpBtn);
        Controls.Add(_hdrRefresh);

        _navFw = NavButton("Firmware", 24);
        _navBal = NavButton("Balance", 24 + 130);
        _navFw.Click += (s, e) => ShowPage(true);
        _navBal.Click += (s, e) =>
        {
            ShowPage(false);
            if (!_balAutoReadDone) OnRead(this, EventArgs.Empty);
        };
        Controls.Add(_navFw);
        Controls.Add(_navBal);

        _pageFw = new Panel { Location = new Point(0, 128), Size = new Size(560, 512), BackColor = BG };
        _pageBal = new Panel { Location = new Point(0, 128), Size = new Size(560, 512), BackColor = BG };
        Controls.Add(_pageFw);
        Controls.Add(_pageBal);

        BuildFirmwarePage();
        BuildBalancePage();

        RefreshVersions();
        WatchFirmwareFolders();
        ShowPage(true);
        RefreshHeadsetInfo();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        _pollTimer.Tick += (s, e) => RefreshHeadsetInfo();
        _pollTimer.Start();
    }

    // Keep the Version list live when firmware files are added/removed.
    void WatchFirmwareFolders()
    {
        foreach (var dir in new[] { FwDir, CustomDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir, "*.bin")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler refresh = (s, e) =>
                {
                    try { BeginInvoke((Action)RefreshVersions); } catch { }
                };
                w.Created += refresh;
                w.Deleted += refresh;
                w.Changed += refresh;
                w.Renamed += (s, e) => { try { BeginInvoke((Action)RefreshVersions); } catch { } };
                _watchers.Add(w);
            }
            catch { }
        }
    }

    // ---------- header: headset + dongle firmware ----------
    void SetHdrLine(Label lbl, string name, string ver, bool show)
    {
        lbl.Visible = show;
        if (!show) { lbl.Text = ""; return; }
        lbl.Text = ver != null ? $"{name}    v{ver}" : $"{name}    -";
        lbl.ForeColor = ver != null ? FG : MUTED;
    }

    async void RefreshHeadsetInfo()
    {
        if (_hdrRefresh == null || _flashing || _resetting) return;
        if (_hdrBusy) { _hdrPending = true; return; }   // queue, don't drop the click
        _hdrBusy = true;
        _hdrHeadset.Visible = true;
        _hdrHeadset.Text = "Checking...";
        _hdrHeadset.ForeColor = MUTED;
        try
        {
            var conn = await Task.Run(HidRace.ProbeConnection);
            if (!conn.Any)
            {
                _headsetVer = _dongleVer = null;
                _hdrHeadset.Visible = true;
                _hdrHeadset.Text = "No device detected";
                _hdrHeadset.ForeColor = MUTED;
                _hdrDongle.Visible = false;
                _platformLabel.Text = "no device - connect headset or dongle";
                _platformLabel.ForeColor = MUTED;
                return;
            }

            if (conn.IsPs != _isPs) { _isPs = conn.IsPs; RefreshVersions(); }
            _platformLabel.Text = (conn.IsPs ? "PlayStation" : "Xbox") + "  (detected)";
            _platformLabel.ForeColor = FG;

            bool dongle = conn.DongleConnected && !conn.HeadsetUsbConnected;
            if (dongle)
            {
                // over the dongle: SDK target 0 = dongle, target 1 = the headset
                var (dv, hv) = await Task.Run(() => SdkVersions.Read(true));
                _dongleVer = dv;
                _headsetVer = hv;
            }
            else
            {
                // headset cabled over USB-C: direct RACE flash read
                var vr = await Task.Run(HidRace.ReadFirmwareVersion);
                _headsetVer = vr.Found ? vr.Version : null;
                _dongleVer = null;
            }

            SetHdrLine(_hdrHeadset, "Headset", _headsetVer, true);
            SetHdrLine(_hdrDongle, "Dongle", _dongleVer, dongle);
            UpdateFwWarning();   // installed versions changed - re-evaluate
        }
        finally
        {
            _hdrBusy = false;
            if (_hdrPending) { _hdrPending = false; RefreshHeadsetInfo(); }
        }
    }

    Button NavButton(string text, int x)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, 86),
            Size = new Size(126, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = FG,
            BackColor = PANEL,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    static Button Accent(string text, Color color, Point loc, Size size)
    {
        var b = new Button
        {
            Text = text,
            Location = loc,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = Color.White,
            BackColor = color,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.18f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, 0.12f);
        return b;
    }

    static Label Caption(string text, Point loc) =>
        new Label { Text = text, Location = loc, AutoSize = true, ForeColor = MUTED };

    ComboBox Combo(Point loc, int width) =>
        new ComboBox
        {
            Location = loc,
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = FIELD,
            ForeColor = FG,
        };

    TextBox LogBox(Point loc, Size size) =>
        new TextBox
        {
            Location = loc,
            Size = size,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = FIELD,
            ForeColor = MUTED,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
        };

    void ShowPage(bool firmware)
    {
        _pageFw.Visible = firmware;
        _pageBal.Visible = !firmware;
        _navFw.BackColor = firmware ? ACCENT : PANEL;
        _navBal.BackColor = firmware ? PANEL : ACCENT;
    }

    // ---------- firmware page ----------
    void BuildFirmwarePage()
    {
        var p = _pageFw;
        _platformLabel = new Label
        {
            Text = "detecting...",
            Location = new Point(140, 17),
            AutoSize = true,
            ForeColor = FG,
            Font = new Font("Segoe UI Semibold", 9.5f),
        };

        _target = Combo(new Point(140, 54), 200);
        _target.Items.AddRange(new object[] { "Dongle", "Headset" });
        _target.SelectedIndex = 1;   // headset is the common case
        _target.SelectedIndexChanged += (s, e) => UpdateFwWarning();

        _version = Combo(new Point(140, 92), 150);
        _version.SelectedIndexChanged += (s, e) => UpdateFwWarning();
        _version.DropDown += (s, e) => RefreshVersions();   // rescan folders on open

        _removeBtn = new Button
        {
            Text = "Remove",
            Location = new Point(296, 92),
            Size = new Size(62, 22),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8f),
            ForeColor = FG,
            BackColor = PANEL,
            Cursor = Cursors.Hand,
        };
        _removeBtn.FlatAppearance.BorderSize = 0;
        _removeBtn.Click += (s, e) => ShowRemoveCustomDialog();

        _flashBtn = Accent("Flash Firmware", ACCENT, new Point(360, 21), new Size(170, 64));
        _flashBtn.Font = new Font("Segoe UI Semibold", 11f);
        _flashBtn.Click += OnFlash;

        _resetBtn = Accent("Factory Reset", WARN, new Point(360, 89), new Size(170, 30));
        _resetBtn.Click += OnReset;

        _fwWarn = new Label
        {
            Location = new Point(24, 134),
            Size = new Size(512, 48),
            ForeColor = WARN,
            Visible = false,
        };
        _fwStatus = new Label
        {
            Text = "Ready. Pick a version and click Flash.",
            Location = new Point(24, 188),
            Size = new Size(512, 20),
            ForeColor = MUTED,
        };
        _fwProgress = new ProgressBar
        {
            Location = new Point(24, 212),
            Size = new Size(512, 8),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
        };
        _fwLog = LogBox(new Point(24, 232), new Size(512, 256));

        p.Controls.AddRange(new Control[]
        {
            Caption("Platform", new Point(24, 19)), _platformLabel,
            Caption("Target", new Point(24, 57)), _target,
            Caption("Version", new Point(24, 95)), _version, _removeBtn,
            _flashBtn, _resetBtn, _fwWarn, _fwStatus, _fwProgress, _fwLog,
        });
    }

    void RefreshVersions()
    {
        if (_version == null) return;
        string sel = _version.Text;
        _version.Items.Clear();
        _customFw.Clear();

        var plat = _isPs ? "PS4" : "XBOX";
        if (Directory.Exists(FwDir))
        {
            var versions = Directory.GetFiles(FwDir, "*.bin")
                .Select(Path.GetFileName)
                .Where(f => f.Contains("v1.0.1.") && f.ToUpper().Contains(plat))
                .Select(f => { int i = f.IndexOf("v1.0.1."); int e = f.IndexOf('_', i + 7); return e > 0 ? f.Substring(i, e - i) : ""; })
                .Where(v => v.Length > 0)
                .Distinct()
                .OrderByDescending(v => v);
            foreach (var v in versions) _version.Items.Add(v);
        }
        if (Directory.Exists(CustomDir))
        {
            foreach (var f in Directory.GetFiles(CustomDir, "*.bin"))
            {
                string disp = "Custom " + Path.GetFileNameWithoutExtension(f);
                _customFw[disp] = f;
                _version.Items.Add(disp);
            }
        }
        if (_version.Items.Count > 0)
        {
            int idx = _version.Items.IndexOf(sel);
            _version.SelectedIndex = idx >= 0 ? idx : 0;
        }
        UpdateFwWarning();
    }

    void UpdateFwWarning()
    {
        if (_version == null || _fwWarn == null) return;

        // warn only when the picked firmware is the version ALREADY installed
        // on the device being flashed (a same-version flash genuinely fails).
        string file = FindFirmwareFile();
        string selVer = file != null ? ReadBinVersion(file) : null;
        bool flashHeadset = _customFw.ContainsKey(_version.Text) || _target.SelectedIndex == 1;
        string installed = flashHeadset ? _headsetVer : _dongleVer;
        bool same = selVer != null && installed != null && selVer == installed;
        _fwWarn.Visible = same;
        _fwWarn.Text = same
            ? $"This {(flashHeadset ? "headset" : "dongle")} is already on v{installed} - flashing the\n"
              + "same version will fail. Flash v1.0.1.63 first, then flash this."
            : "";
    }

    void ShowRemoveCustomDialog()
    {
        List<string> Scan() => Directory.Exists(CustomDir)
            ? Directory.GetFiles(CustomDir, "*.bin").OrderBy(f => f).ToList()
            : new List<string>();
        var files = Scan();

        using var dlg = new Form
        {
            Text = "Custom Firmware",
            ClientSize = new Size(440, 344),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            BackColor = BG,
            ForeColor = FG,
            Font = new Font("Segoe UI", 9.5f),
        };
        var head = new Label
        {
            Location = new Point(16, 14),
            Size = new Size(408, 20),
            ForeColor = MUTED,
        };
        var clb = new CheckedListBox
        {
            Location = new Point(16, 40),
            Size = new Size(408, 224),
            BackColor = FIELD,
            ForeColor = FG,
            BorderStyle = BorderStyle.FixedSingle,
            CheckOnClick = true,
            IntegralHeight = false,
            Font = new Font("Segoe UI", 9.5f),
        };
        void Reload()
        {
            files = Scan();
            clb.Items.Clear();
            foreach (var f in files) clb.Items.Add(Path.GetFileName(f));
            head.Text = files.Count > 0
                ? "Tick the custom firmwares to delete, then Delete Checked."
                : "No custom firmwares saved.";
        }
        Reload();

        var del = Accent("Delete Checked", WARN, new Point(16, 284), new Size(170, 40));
        del.Click += (s, e) =>
        {
            // Checking the boxes + clicking Delete is the deliberate action -
            // no extra confirm dialog (a nested dialog here would deadlock the
            // in-window overlay, which lives on the disabled parent window).
            var picked = clb.CheckedIndices.Cast<int>().Select(i => files[i]).ToList();
            if (picked.Count == 0) return;
            foreach (var f in picked) { try { File.Delete(f); } catch { } }
            Reload();
        };
        var close = Accent("Close", ACCENT, new Point(312, 284), new Size(112, 40));
        close.DialogResult = DialogResult.OK;

        dlg.Controls.AddRange(new Control[] { head, clb, del, close });
        dlg.AcceptButton = close;
        dlg.ShowDialog(this);
        RefreshVersions();
    }

    string FindFirmwareFile()
    {
        if (_customFw.TryGetValue(_version.Text, out string custom)) return custom;
        var plat = _isPs ? "PS4" : "XBOX";
        var tgt = _target.SelectedIndex == 0 ? "dongle" : "headset";
        if (Directory.Exists(FwDir))
        {
            var files = Directory.GetFiles(FwDir, $"*{_version.Text}*{plat}*{tgt}*");
            if (files.Length > 0) return files[0];
        }
        return null;
    }

    void FwLog(string m)
    {
        if (InvokeRequired) { Invoke(() => FwLog(m)); return; }
        _fwLog.AppendText(m + Environment.NewLine);
    }
    void FwStatus(string m)
    {
        if (InvokeRequired) { Invoke(() => FwStatus(m)); return; }
        _fwStatus.Text = m;
    }

    async void OnFlash(object sender, EventArgs e)
    {
        if (_flashing || _resetting) return;
        var fwFile = FindFirmwareFile();
        if (fwFile == null)
        {
            Dlg("Firmware file not found.\nCheck the firmware/ folder.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        bool custom = _customFw.ContainsKey(_version.Text);
        bool flashHeadset = custom || _target.SelectedIndex == 1;   // 1 = Headset

        // ---- pre-flight checks: stop the common mistakes before they cost 10 minutes ----
        _flashBtn.Enabled = false;
        var conn = await Task.Run(HidRace.ProbeConnection);
        _flashBtn.Enabled = true;

        if (!conn.Any)
        {
            Dlg(
                "No Maxwell device detected.\n\n"
                + "Connect the headset with a USB-C cable (or plug in the dongle),\n"
                + "then try again.",
                "Nothing Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (flashHeadset && conn.DongleConnected)
        {
            Dlg(
                "Unplug the wireless dongle first.\n\n"
                + "You are flashing the HEADSET, but the USB dongle is plugged in.\n"
                + "With the dongle connected the flash goes to the wrong device.\n\n"
                + "1. Unplug the USB dongle from the PC.\n"
                + "2. Connect the headset directly with a USB-C cable.\n"
                + "3. Try again.",
                "Unplug the Dongle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (flashHeadset && !conn.HeadsetUsbConnected)
        {
            Dlg(
                "Connect the headset with a USB-C cable.\n\n"
                + "You are flashing the HEADSET, but no cabled headset is detected.\n"
                + "Plug the headset into the PC with a USB-C cable and try again.",
                "Connect the Headset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!flashHeadset && !conn.DongleConnected)
        {
            Dlg(
                "Connect the wireless dongle.\n\n"
                + "You are flashing the DONGLE, but no dongle is detected.\n"
                + "Plug the USB dongle into the PC, power the headset on, then retry.",
                "Connect the Dongle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // same-version check — compare the firmware against the installed
        // version of the device being flashed.
        string targetVer = ReadBinVersion(fwFile);
        string installedVer = flashHeadset ? _headsetVer : _dongleVer;
        if (installedVer != null && targetVer != null && targetVer == installedVer)
        {
            Dlg(
                "This flash will FAIL.\n\n"
                + $"This device is already on v{installedVer}, and you cannot flash\n"
                + "the version that is already installed.\n\n"
                + "To install this firmware:\n"
                + "1. Flash stock v1.0.1.63 first (pick it in the Version list).\n"
                + "2. Then flash this one.",
                "Same Version - Won't Flash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // ---- confirmation ----
        var msg = !flashHeadset
            ? "For a DONGLE update:\n"
              + "- USB dongle plugged into the PC\n"
              + "- Headset powered ON and wirelessly connected to the dongle\n"
              + "- Audeze app closed"
            : "HEADSET update - get the headset into the right state first:\n"
              + "1. Unplug the wireless dongle from the PC.\n"
              + "2. Turn the headset fully OFF - no lights on it at all.\n"
              + "   (Any light while the cable is unplugged means it is NOT off.)\n"
              + "3. With it off, wait about 5 seconds.\n"
              + "4. Plug the USB-C cable into the headset.\n"
              + "Close the Audeze app. When the headset is in this off-but-cabled\n"
              + "state the top-right shows its firmware version - once you see\n"
              + "that, it is connected and ready to flash.\n\n"
              + "If a flash fails: unplug the USB-C cable, wait 5-10 seconds with\n"
              + "the headset off, plug it back in, and retry.";
        if (custom)
            msg += "\n\nAfter this custom firmware flashes you MUST run a Factory Reset\n"
                 + "for the new balance to take effect - the tool will guide you.";

        if (Dlg($"Flash {Path.GetFileName(fwFile)}?\n\n{msg}\n\nDO NOT disconnect anything during the flash!",
            "Confirm Flash", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        // baked-in balance values, parsed from the custom firmware's file name
        int bakedL = -1, bakedR = -1;
        if (custom)
        {
            var m = Regex.Match(Path.GetFileName(fwFile), @"L(\d+)_R(\d+)");
            if (m.Success) { bakedL = int.Parse(m.Groups[1].Value); bakedR = int.Parse(m.Groups[2].Value); }
        }

        // ---- flash ----
        _flashing = true;
        _flashBtn.Enabled = false;
        EnableResetButtons(false);
        _fwProgress.MarqueeAnimationSpeed = 30;
        _fwLog.Clear();
        ShowPage(true);
        await Task.Run(() => DoFlash(fwFile, custom));
        _fwProgress.MarqueeAnimationSpeed = 0;
        bool flashOk = _done || _rebooting;

        if (flashOk && custom)
        {
            ShowFlashDoneNotice();            // blocks until OK is clicked
            await PostCustomFlash(bakedL, bakedR);   // checking overlay opens at once
        }
        else
        {
            if (flashOk) ShowFlashDoneStock();
            await Task.Delay(8000);           // let the headset finish rebooting (~7-8 s)
            RefreshHeadsetInfo();
        }

        _flashBtn.Enabled = true;
        EnableResetButtons(true);
        _flashing = false;
        RefreshHeadsetInfo();   // header was frozen during the flash - refresh now
    }

    // After a custom-firmware flash: go to the Balance tab, re-check the
    // headset, read the balance, and push the user to factory-reset if the
    // baked-in values are not active yet (they are not, until a reset runs).
    async Task PostCustomFlash(int bakedL, int bakedR)
    {
        ShowPage(false);
        _balLog.Clear();   // start the Balance log clean - no flash/reboot noise
        BalLog("Custom firmware flashed. Waiting for reboot, then checking balance...");

        // Block the UI while we wait for the reboot and read the balance, so
        // nothing can be clicked mid-check. Hidden before the result popup.
        var busy = ShowBusy("Checking the Headset",
            "The custom firmware flashed successfully.\n\n"
            + "Waiting for the headset to reboot, then reading its balance\n"
            + "values to confirm. Please wait - this can take up to a minute.\n\n"
            + "Do not unplug the headset or click anything until this finishes.");
        HidRace.Result res;
        try
        {
            await Task.Delay(8000);
            RefreshHeadsetInfo();
            res = await Task.Run(() => HidRace.ReadBalance());
        }
        finally
        {
            HideBusy(busy);
        }

        bool known = bakedL >= 0 && bakedR >= 0;
        string detail;
        if (!res.Ok)
        {
            BalLog("Could not read the balance back: " + res.Message);
            detail = "The tool could not read the balance back to confirm it,\n"
                   + "but the factory reset below is still what activates it.\n\n";
        }
        else
        {
            _balCurrent.Text = $"Balance:  L {res.L}   R {res.R}";
            if (known && res.L == bakedL && res.R == bakedR)
            {
                BalLog($"Balance is L={res.L} R={res.R} - matches the custom firmware. Done.");
                Dlg($"The custom firmware is flashed and its balance is already\n"
                    + $"active (L = {res.L}   R = {res.R}). Nothing more to do.",
                    "Custom Firmware Active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            BalLog(known
                ? $"Headset reads L={res.L} R={res.R}; custom firmware has L={bakedL} R={bakedR}."
                : $"Headset reads L={res.L} R={res.R}.");
            detail = known
                ? $"Headset now reads:    L = {res.L}   R = {res.R}\n"
                  + $"Custom firmware has:  L = {bakedL}   R = {bakedR}\n\n"
                : $"Headset now reads:  L = {res.L}   R = {res.R}\n\n";
        }

        if (Dlg(
            "The custom firmware is flashed, but its balance is NOT active yet.\n\n"
            + detail
            + "Click Run Factory Reset to apply it now. The headset will\n"
            + "reboot and re-connect on its own.",
            "Factory Reset Needed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            "Run Factory Reset", "Later") == DialogResult.Yes)
        {
            await DoFactoryReset();
        }
    }

    // Read the version string out of a firmware .bin (TLV 0x0013 header field).
    static string ReadBinVersion(string path)
    {
        try
        {
            byte[] buf = new byte[0x140];
            using (var fs = File.OpenRead(path))
                if (fs.Read(buf, 0, buf.Length) < buf.Length) return null;
            if (buf[0x10E] != 0x13 || buf[0x10F] != 0x00) return null;
            var sb = new StringBuilder();
            for (int p = 0x112; p < buf.Length && buf[p] >= 0x20 && buf[p] < 0x7F; p++)
                sb.Append((char)buf[p]);
            var m = Regex.Match(sb.ToString(), @"(\d+\.\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    // Plain (non-custom) flash success. Carries a yellow note: if this was
    // the v1.0.1.63 downgrade done before a custom flash, restart the headset
    // first - it makes the next flash far more reliable.
    void ShowFlashDoneStock()
    {
        using var dlg = new Form
        {
            Text = "Flash Complete",
            ClientSize = new Size(470, 300),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            BackColor = BG,
            ForeColor = FG,
            Font = new Font("Segoe UI", 9.5f),
        };
        var head = new Label
        {
            Text = "FLASH COMPLETE",
            Location = new Point(26, 22),
            AutoSize = true,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = GREEN,
        };
        var body = new Label
        {
            Text = "The headset is rebooting - give it about 8 seconds.",
            Location = new Point(26, 64),
            Size = new Size(418, 22),
            ForeColor = MUTED,
        };
        var noteLead = new Label
        {
            Text = "Installing custom firmware next?",
            Location = new Point(26, 100),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = WARN,
        };
        var note = new Label
        {
            Text = "If you just downgraded to v1.0.1.63 so you can flash the\n"
                 + "custom v1.0.1.74, restart the headset before that flash:\n"
                 + "unplug it, make sure NO lights are on (fully off when\n"
                 + "unplugged), then plug the USB-C cable back in WITHOUT\n"
                 + "powering it on. Flashing from that state succeeds far\n"
                 + "more reliably.",
            Location = new Point(26, 124),
            Size = new Size(418, 116),
            ForeColor = WARN,
        };
        var ok = Accent("OK", ACCENT, new Point(155, 248), new Size(160, 38));
        ok.DialogResult = DialogResult.OK;
        dlg.Controls.AddRange(new Control[] { head, body, noteLead, note, ok });
        dlg.AcceptButton = ok;
        dlg.ShowDialog(this);
    }

    void ShowFlashDoneNotice()
    {
        using var dlg = new Form
        {
            Text = "Flash Complete",
            ClientSize = new Size(470, 268),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            BackColor = BG,
            ForeColor = FG,
            Font = new Font("Segoe UI", 9.5f),
        };
        var head = new Label
        {
            Text = "FLASH COMPLETE",
            Location = new Point(26, 22),
            AutoSize = true,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            ForeColor = GREEN,
        };
        var big = new Label
        {
            Text = "You must run a FACTORY RESET now,\nor the new balance will NOT apply.",
            Location = new Point(26, 66),
            Size = new Size(418, 62),
            Font = new Font("Segoe UI Semibold", 13.5f),
            ForeColor = WARN,
        };
        var body = new Label
        {
            Text = "Click OK and the tool will check the headset, then show a\n"
                 + "prompt with a Run Factory Reset button. That reset is the\n"
                 + "final step - it activates your new balance.",
            Location = new Point(26, 138),
            Size = new Size(418, 66),
            ForeColor = MUTED,
        };
        var ok = Accent("OK  -  continue", ACCENT, new Point(155, 212), new Size(160, 38));
        ok.DialogResult = DialogResult.OK;
        dlg.Controls.AddRange(new Control[] { head, big, body, ok });
        dlg.AcceptButton = ok;
        dlg.ShowDialog(this);
    }

    // ---------- in-app guide ----------
    const string GUIDE =
@"MAXWELL TOOL  -  GUIDE

WHAT THIS TOOL DOES
  - Corrects the Maxwell's left/right audio balance and bakes the fix
    permanently into the headset's firmware, so it works on any device
    (phone, console, PC) with nothing else connected.
  - Flashes and downgrades headset / dongle firmware.

BEFORE YOU START
  - For the balance features, connect the headset to the PC with a
    USB-C cable. The wireless dongle cannot read the headset's memory.
  - Close the Audeze app.
  - Keep the headset charged above ~50% before flashing.

TOP-RIGHT DISPLAY
  Shows the connected device and its firmware version. Click ""Recheck""
  to refresh it. Use it to see whether you must downgrade before
  flashing (see the same-version rule below).

FIXING THE L/R BALANCE   (Balance tab)
  IMPORTANT: tune the balance while the headset is running firmware
  v1.0.1.74. The imbalance is specific to that version, and the custom
  firmware you build is a v1.0.1.74 - tuning on any other version gives
  the wrong values. Check the top-right version; flash v1.0.1.74 first
  if you are not on it.
  1. Connect the headset to the PC with the USB-C cable.
  2. Click ""Read Balance"" - it shows the current Left and Right values.
  3. Adjust Left and Right. Lower the channel that is too loud, or raise
     the one that is too quiet, in small steps (1-3 at a time).
     MAXIMUM 150 - never go higher. High gain can damage a driver.
  4. Click ""Apply (live test)"" - the values take effect immediately.
     Play centred audio (a mono track or vocals) and judge the balance.
     Repeat steps 3-4 until it sounds centred.
     Applied values are SAVED on the headset: they survive power-offs
     and work on every device. Only a factory reset or a fully drained
     battery clears them.
  5. When it sounds right, click ""Make Custom Firmware"". It bakes the
     values into a flashable firmware and switches you to the Firmware
     tab with the new file already selected.
  6. Click ""Flash Firmware"". If the headset is already on v1.0.1.74,
     the tool stops you - flash stock v1.0.1.63 first, then flash the
     custom firmware.
  7. After the flash the tool reads the balance back and prompts you to
     run a Factory Reset. This is REQUIRED - the baked-in balance only
     takes effect after a factory reset.

FLASHING FIRMWARE   (Firmware tab)
  - Pick Target (Headset or Dongle) and Version. The Platform
    (Xbox / PlayStation) is detected automatically.
  - A flash takes about 5-10 minutes. Do NOT disconnect anything.

  HEADSET FLASH - get the headset into the right state first. This is the
  biggest factor in whether the flash succeeds:
    1. Unplug the wireless dongle from the PC.
    2. Turn the headset fully OFF - no lights on it at all. (Any light
       while the USB-C cable is unplugged means it is NOT off.)
    3. With the headset off, wait about 5 seconds.
    4. Plug the USB-C cable into the headset.
  Close the Audeze app. In this off-but-cabled state the top-right shows
  the headset's firmware version - once you see it, it is ready to flash.
  If a flash fails, return to that state: unplug the USB-C cable, wait
  5-10 seconds with the headset off, plug it back in, then retry.

  DONGLE FLASH: plug the dongle into the PC, headset powered on, Audeze
  app closed.

  SAME-VERSION RULE: you cannot flash the version already installed.
  To reinstall / install a custom v1.0.1.74, flash v1.0.1.63 first,
  then flash the v1.0.1.74. The tool checks this before flashing.

FACTORY RESET
  - The ""Factory Reset"" button restores the headset to factory defaults.
  - REQUIRED after flashing a custom firmware, so the new balance applies.
  - The headset reboots and reconnects on its own (about 15-20 seconds).

TROUBLESHOOTING
  - No device / blank version: connect the headset by USB-C, close the
    Audeze app, click Recheck.
  - Flash will not start: the tool checks the connection first - just
    follow the on-screen message (unplug the dongle, connect USB-C...).
  - Flash fails or hangs: unplug USB-C, power the headset off, wait 10
    seconds, power it on, wait 20 seconds, reconnect, and retry - try
    it with the headset powered off.

SAFETY
  - Never set a balance value above 150. High gain can overdrive and
    permanently damage a driver.
  - Never disconnect anything during a flash.";

    void ShowGuide()
    {
        using var dlg = new Form
        {
            Text = "Maxwell Tool - Guide",
            ClientSize = new Size(624, 584),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            BackColor = BG,
            ForeColor = FG,
            Font = new Font("Segoe UI", 9.5f),
        };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(16, 16),
            Size = new Size(592, 514),
            BackColor = FIELD,
            ForeColor = FG,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Text = GUIDE.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
        };
        box.Select(0, 0);
        var close = Accent("Close", ACCENT, new Point(256, 540), new Size(112, 32));
        close.DialogResult = DialogResult.OK;
        dlg.Controls.Add(box);
        dlg.Controls.Add(close);
        dlg.AcceptButton = close;
        dlg.ShowDialog(this);
    }

    // In-window modal dialog: a panel overlay drawn over the tool's own client
    // area - never a separate OS window, so it can't land on another monitor.
    // Same signature as MessageBox.Show, so call sites are a drop-in swap.
    DialogResult Dlg(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
                     string primaryText = null, string secondaryText = null)
    {
        Color accent = icon switch
        {
            MessageBoxIcon.Error => RED,
            MessageBoxIcon.Warning => WARN,
            MessageBoxIcon.Question => ACCENT,
            _ => GREEN,
        };
        var head = new Label
        {
            Text = caption,
            AutoSize = true,
            Location = new Point(24, 20),
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = accent,
        };
        var body = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Location = new Point(24, 24 + head.PreferredSize.Height + 10),
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = FG,
        };
        int btnY = body.Location.Y + body.PreferredSize.Height + 22;

        var result = DialogResult.None;
        var btns = new List<Button>();
        Color secondary = Color.FromArgb(66, 68, 78);
        void MakeBtn(string t, Color bg, bool primary, DialogResult res)
        {
            var f = new Font("Segoe UI Semibold", 10f);
            var b = new Button
            {
                Text = t,
                Size = new Size(Math.Max(116, TextRenderer.MeasureText(t, f).Width + 34), 34),
                FlatStyle = FlatStyle.Flat,
                Font = f,
                ForeColor = primary ? Color.White : FG,
                BackColor = bg,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.16f);
            b.Click += (s, e) => result = res;
            btns.Add(b);
        }
        if (buttons == MessageBoxButtons.OKCancel)
        {
            MakeBtn(primaryText ?? "OK", accent, true, DialogResult.OK);
            MakeBtn(secondaryText ?? "Cancel", secondary, false, DialogResult.Cancel);
        }
        else if (buttons == MessageBoxButtons.YesNo)
        {
            MakeBtn(primaryText ?? "Yes", accent, true, DialogResult.Yes);
            MakeBtn(secondaryText ?? "No", secondary, false, DialogResult.No);
        }
        else
        {
            MakeBtn(primaryText ?? "OK", accent, true, DialogResult.OK);
        }

        int gap = 14;
        int totalBtnW = (btns.Count - 1) * gap;
        foreach (var b in btns) totalBtnW += b.Width;
        int cardW = Math.Min(560, Math.Max(320,
            Math.Max(head.PreferredSize.Width, body.PreferredSize.Width) + 48));
        cardW = Math.Max(cardW, totalBtnW + 48);
        int cardH = btnY + 34 + 22;

        var card = new Panel { Size = new Size(cardW, cardH), BackColor = PANEL };
        int bx = (cardW - totalBtnW) / 2;
        foreach (var b in btns) { b.Location = new Point(bx, btnY); bx += b.Width + gap; card.Controls.Add(b); }
        card.Controls.Add(head);
        card.Controls.Add(body);

        var backdrop = new Panel
        {
            Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
            BackColor = Color.FromArgb(14, 14, 17),
        };
        card.Location = new Point((backdrop.Width - cardW) / 2, (backdrop.Height - cardH) / 2);
        backdrop.Controls.Add(card);
        Controls.Add(backdrop);
        backdrop.BringToFront();
        btns[0].Focus();

        while (result == DialogResult.None && !IsDisposed)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        if (!IsDisposed) Controls.Remove(backdrop);
        backdrop.Dispose();
        return result == DialogResult.None ? DialogResult.Cancel : result;
    }

    // A modal "please wait" overlay - same backdrop as Dlg, but no buttons and
    // no wait loop. The backdrop covers the client area and blocks all input
    // to the UI behind it. The caller does its (awaited) work, so the message
    // loop keeps the overlay painted, then calls HideBusy to remove it.
    Panel ShowBusy(string caption, string text)
    {
        var head = new Label
        {
            Text = caption,
            AutoSize = true,
            Location = new Point(24, 20),
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = ACCENT,
        };
        var body = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Location = new Point(24, 24 + head.PreferredSize.Height + 10),
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = FG,
        };
        int barY = body.Location.Y + body.PreferredSize.Height + 20;
        int cardW = Math.Min(552, Math.Max(380,
            Math.Max(head.PreferredSize.Width, body.PreferredSize.Width) + 48));
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(24, barY),
            Size = new Size(cardW - 48, 10),
        };
        int cardH = barY + 10 + 22;

        var card = new Panel { Size = new Size(cardW, cardH), BackColor = PANEL };
        card.Controls.Add(head);
        card.Controls.Add(body);
        card.Controls.Add(bar);

        var backdrop = new Panel
        {
            Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
            BackColor = Color.FromArgb(14, 14, 17),
        };
        card.Location = new Point((backdrop.Width - cardW) / 2, (backdrop.Height - cardH) / 2);
        backdrop.Controls.Add(card);
        Controls.Add(backdrop);
        backdrop.BringToFront();
        return backdrop;
    }

    void HideBusy(Panel backdrop)
    {
        if (backdrop != null && !IsDisposed) Controls.Remove(backdrop);
        backdrop?.Dispose();
    }

    void DoFlash(string fwFile, bool custom)
    {
        _readyToApply = _done = _applying = _rebooting = _failed = false;
        _callback = OnSDKCallback;
        FwLog($"Firmware: {Path.GetFileName(fwFile)}");
        FwStatus("Connecting to device...");

        int result = -1;
        foreach (var pid in PIDS)
        {
            result = AirohaSDK.initializeAirohaSDK(0x3329, pid);
            if (result == 1) { FwLog("Connected: " + PidName(pid)); break; }
            try { AirohaSDK.closeAirohaSDK(); } catch { }
            Thread.Sleep(500);
        }
        if (result != 1)
        {
            FwLog("FAILED to connect to device.");
            FwStatus("Connection failed. Check the device and retry.");
            return;
        }
        try
        {
            AirohaSDK.setTargetDevice(0);
            AirohaSDK.registerUpdateResultCallback(_callback);
            FwStatus("Preparing firmware transfer...");
            FwLog("Requesting firmware-update info...");
            AirohaSDK.requestDFUInfo();
            Thread.Sleep(5000);
            AirohaSDK.setDfuMode(0);
            AirohaSDK.setBatteryLevel(20);
            AirohaSDK.setDfuAgentFilepath(Path.GetFullPath(fwFile));
            AirohaSDK.setPingTimerFlag(0);
            FwStatus("Transferring firmware - DO NOT DISCONNECT!");
            FwLog("Transfer started. This usually takes 5-10 minutes - be patient.");
            FwLog("Do not disconnect the headset or close this window.");
            FwLog("");
            AirohaSDK.startDataTransfer();

            for (int i = 0; i < 900; i++)
            {
                Thread.Sleep(1000);
                if (_failed)
                {
                    FwStatus("Flash failed. Close and retry.");
                    FwLog("");
                    FwLog("*** FLASH FAILED (FAIL/TIMEOUT) - stopping. ***");
                    FwLog("Unplug the USB-C cable, wait 5-10 seconds with the");
                    FwLog("headset fully off, then plug it back in and retry.");
                    break;
                }
                if (_readyToApply)
                {
                    _applying = true;
                    FwStatus("Applying firmware - device is rebooting...");
                    FwLog("Applying firmware update...");
                    AirohaSDK.applyNewFirmware(20);
                    _readyToApply = false;
                }
                if (_rebooting)
                {
                    FwStatus("Firmware update complete - device rebooting!");
                    FwLog("");
                    FwLog("*** SUCCESS! Firmware applied, device is rebooting. ***");
                    break;
                }
                if (_done)
                {
                    FwStatus("Firmware update complete!");
                    FwLog("");
                    FwLog("*** SUCCESS! Firmware updated. ***");
                    break;
                }
                if (i > 0 && i % 30 == 0)
                    FwLog(_applying ? $"  Applying... ({i}s)" : $"  Transferring... ({i}s)");
            }
            if (!_done && !_rebooting && !_failed)
            {
                FwStatus("Timed out. Close and retry.");
                FwLog("Timed out waiting for completion.");
            }
            Thread.Sleep(3000);
            AirohaSDK.closeAirohaSDK();
        }
        catch (Exception ex)
        {
            FwLog($"Error: {ex.Message}");
            FwStatus("Error occurred. Close and retry.");
        }
    }

    void OnSDKCallback(int status, int msgId, IntPtr extra)
    {
        if (status == 5) { _readyToApply = true; FwLog("  [READY_TO_APPLY]"); }
        else if (status == 1) { _done = true; FwLog("  [DFU_SUCCESS]"); }
        else if (status == 2) { _failed = true; FwLog("  [FAIL/TIMEOUT]"); }
        else if (status == 6) { _applying = true; FwLog("  [APPLYING...]"); }
        else if (status == 7) { _rebooting = true; FwLog("  [REBOOTING...]"); }
    }

    // ---------- factory reset ----------
    AirohaSDK.UpdateCallback _resetCb;

    // The reset button appears on both pages; enable/disable them together.
    void EnableResetButtons(bool on)
    {
        _resetBtn.Enabled = on;
        _resetBtn2.Enabled = on;
    }

    async void OnReset(object sender, EventArgs e)
    {
        if (_flashing || _resetting) return;
        if (Dlg(
            "Factory-reset the headset?\n\n"
            + "This restores the headset to factory defaults: it clears EQ presets,\n"
            + "sidetone and sound settings, and re-applies the firmware's built-in\n"
            + "values - including the L/R balance baked into a custom firmware.\n\n"
            + "Connect the headset by USB-C cable first. It will reboot and\n"
            + "re-connect on its own (about 15-20 seconds).",
            "Factory Reset", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        await DoFactoryReset();
    }

    async Task DoFactoryReset()
    {
        if (_resetting) return;
        _resetting = true;
        EnableResetButtons(false);
        _flashBtn.Enabled = false;
        _fwProgress.MarqueeAnimationSpeed = 30;
        _fwLog.Clear();
        await Task.Run(DoReset);
        _fwProgress.MarqueeAnimationSpeed = 0;
        EnableResetButtons(true);
        _flashBtn.Enabled = true;
        _resetting = false;
        await Task.Delay(15000);          // 2-stage reset reboot - recheck at ~15 s
        RefreshHeadsetInfo();
    }

    void DoReset()
    {
        _resetCb = (status, msgId, extra) => FwLog($"  [SDK status={status} msg=0x{msgId:X}]");
        FwStatus("Connecting to headset...");
        FwLog("Factory reset - connecting to the headset...");

        int result = -1;
        foreach (var pid in PIDS)
        {
            result = AirohaSDK.initializeAirohaSDK(0x3329, pid);
            if (result == 1) { FwLog("Connected: " + PidName(pid)); break; }
            try { AirohaSDK.closeAirohaSDK(); } catch { }
            Thread.Sleep(400);
        }
        if (result != 1)
        {
            FwLog("FAILED to connect. Plug the headset in via USB-C and retry.");
            FwStatus("Connection failed.");
            return;
        }
        try
        {
            AirohaSDK.registerUpdateResultCallback(_resetCb);
            Thread.Sleep(300);
            FwStatus("Sending factory-reset command...");
            FwLog("Sending factory-reset command (SetFactoryResetEx)...");
            int rc = AirohaSDK.SetFactoryResetEx(0);
            FwLog($"SetFactoryResetEx returned {rc}.");
            for (int i = 1; i <= 8; i++)
            {
                Thread.Sleep(1000);
                if (i % 3 == 0) FwLog($"  Working... ({i}s)");
            }
            try { AirohaSDK.closeAirohaSDK(); } catch { }
            FwLog("");
            FwLog("*** Factory-reset command sent. ***");
            FwLog("The headset should reboot now. Wait ~15-20 seconds for it to");
            FwLog("re-connect, then use Recheck (top right) to confirm the");
            FwLog("version, or read the balance on the Balance tab.");
            FwStatus("Factory reset sent - headset rebooting.");
            Invoke(() => Dlg(
                "Factory-reset command sent.\n\n"
                + "The headset will reboot and re-connect on its own (about 15-20 seconds).\n"
                + "If the headset does not reset, factory-reset it from the Audeze app.",
                "Factory Reset", MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        catch (Exception ex)
        {
            FwLog("Error: " + ex.Message);
            FwStatus("Error during factory reset.");
            try { AirohaSDK.closeAirohaSDK(); } catch { }
        }
    }

    // ---------- balance page ----------
    void BuildBalancePage()
    {
        var p = _pageBal;

        _balCurrent = new Label
        {
            Text = "Balance:  L -   R -",
            Location = new Point(24, 18),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = FG,
        };
        _readBtn = Accent("Read Balance", ACCENT, new Point(360, 16), new Size(170, 56));
        _readBtn.Font = new Font("Segoe UI Semibold", 11f);
        _readBtn.Click += OnRead;

        _resetBtn2 = Accent("Factory Reset", WARN, new Point(360, 78), new Size(170, 56));
        _resetBtn2.Font = new Font("Segoe UI Semibold", 11f);
        _resetBtn2.Click += OnReset;

        _srcBtn = new Button
        {
            Text = "Set Audio Source",
            Location = new Point(216, 14),
            Size = new Size(134, 28),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = FG,
            BackColor = PANEL,
            Cursor = Cursors.Hand,
        };
        _srcBtn.FlatAppearance.BorderSize = 0;
        _srcBtn.Click += OnSetSource;

        _balL = Spinner(new Point(140, 60));
        _balR = Spinner(new Point(140, 100));

        _applyBtn = Accent("Apply (live test)", GREEN, new Point(24, 146), new Size(252, 40));
        _applyBtn.Click += OnApply;
        _makeFwBtn = Accent("Make Custom Firmware", ACCENT, new Point(284, 146), new Size(252, 40));
        _makeFwBtn.Click += OnMakeFirmware;

        var hint = new Label
        {
            Text = "Balance over the source you use most - the USB-C cable, or the wireless\n"
                 + "dongle (it also covers Bluetooth). Apply sends L/R live so you can tune\n"
                 + "by ear; the values persist until a factory reset or a dead battery. Make\n"
                 + "Custom Firmware bakes them in for good.   Max 150 per channel.",
            Location = new Point(24, 196),
            Size = new Size(512, 66),
            ForeColor = MUTED,
        };
        var balNote = new Label
        {
            Text = "Just flashed a custom firmware? The balance still shows the OLD values -\n"
                 + "run Factory Reset, then click Read again.",
            Location = new Point(24, 264),
            Size = new Size(512, 36),
            ForeColor = WARN,
        };
        _balLog = LogBox(new Point(24, 304), new Size(512, 184));

        p.Controls.AddRange(new Control[]
        {
            _balCurrent, _srcBtn, _readBtn, _resetBtn2,
            Caption("Left  (0-150)", new Point(24, 63)), _balL,
            Caption("Right (0-150)", new Point(24, 103)), _balR,
            _applyBtn, _makeFwBtn, hint, balNote, _balLog,
        });
    }

    NumericUpDown Spinner(Point loc) =>
        new NumericUpDown
        {
            Location = loc,
            Width = 90,
            Minimum = 0,
            Maximum = 150,           // hard driver-safety cap
            Value = 128,
            BackColor = FIELD,
            ForeColor = FG,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 11f),
            TextAlign = HorizontalAlignment.Center,
        };

    void BalLog(string m)
    {
        if (InvokeRequired) { Invoke(() => BalLog(m)); return; }
        _balLog.AppendText(m + Environment.NewLine);
    }

    async void OnRead(object sender, EventArgs e)
    {
        if (_flashing || _resetting)
        {
            BalLog("A flash or reset is in progress - wait for it to finish.");
            return;
        }
        _readBtn.Enabled = false;
        // the headset needs a moment to settle after a write - a read too soon
        // returns a stale/garbage value, so wait out the rest of a ~3s window.
        long sinceApply = Environment.TickCount64 - _lastApplyTick;
        if (sinceApply < 5000)
        {
            BalLog("Letting the headset settle after Apply...");
            await Task.Delay((int)(5000 - sinceApply));
        }
        var res = await Task.Run(() => HidRace.ReadBalance());
        if (res.Ok)
        {
            _balCurrent.Text = $"Balance:  L {res.L}   R {res.R}";
            // set the L/R input boxes only on the first read (page open); after
            // that they are the user's target and only change when typed.
            if (!_balAutoReadDone)
            {
                if (res.L <= 150) _balL.Value = res.L;
                if (res.R <= 150) _balR.Value = res.R;
            }
            _balAutoReadDone = true;
            BalLog($"Read OK: L={res.L} R={res.R}");

            // Once per session, surface a warning if the headset is stuck on
            // the USB-C audio profile (NVDM 0xF702 = 0x0A). The cache means
            // we only hit the headset for this once - the value never changes
            // in normal operation, so re-reading on every Read is wasteful.
            int src = await EnsureSourceStateAsync();
            if (src == 0x0A)
                BalLog("WARNING: headset is on the USB-C audio profile - click 'Set Audio Source' above to switch to Wireless before calibrating.");
        }
        else BalLog("Read failed: " + res.Message);
        _readBtn.Enabled = true;
    }

    // Get the headset's audio source profile (NVDM 0xF702). Cached for the
    // session because nothing in the firmware ever updates F702 in normal use -
    // it's a static factory-provisioned byte. The cache is cleared only by an
    // app restart or by a successful Set Audio Source write below.
    // Returns -1 on read failure.
    async Task<int> EnsureSourceStateAsync()
    {
        if (_sourceStateCache >= 0) return _sourceStateCache;
        var res = await Task.Run(() => HidRace.ReadSourceState());
        if (res.Ok) _sourceStateCache = res.State;
        return _sourceStateCache;
    }

    // Set the headset's audio source profile to wireless (NVDM 0xF702 = 0).
    // Some headsets ship factory-stuck on the USB-C audio profile (F702 = 0x0A)
    // which carries an asymmetric L/R balance default (141/149) and an older
    // EQ tuning. Wireless is the profile Audeze keeps current and is the
    // correct one for this product regardless of how you connect.
    async void OnSetSource(object sender, EventArgs e)
    {
        if (_flashing || _resetting)
        {
            BalLog("A flash or reset is in progress - wait for it to finish.");
            return;
        }

        var confirm = Dlg(
            "Set the headset's audio source to Wireless?\n\n"
            + "Some Maxwell headsets ship stuck on the USB-C audio profile,\n"
            + "which causes an L/R imbalance and applies an older EQ tuning.\n"
            + "The Wireless profile is the correct one for this product.\n\n"
            + "Even if you use the USB-C cable for audio, the Wireless profile\n"
            + "is still the right one - Audeze keeps it current; the USB-C\n"
            + "profile is older and has known imbalance issues.\n\n"
            + "This sets NVDM 0xF702 = 0x00. The change persists across reboots.",
            "Set Audio Source", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        _srcBtn.Enabled = false;
        BalLog("Setting audio source to Wireless...");
        var res = await Task.Run(() => HidRace.WriteSourceState(0x00));
        if (res.Ok)
        {
            _sourceStateCache = res.State;   // update cache to the now-correct value
            BalLog($"Audio source set to Wireless (0xF702 = 0x{res.State:X2}).");
            Dlg(
                "Audio source set to Wireless.\n\n"
                + "Your headset is now on the correct audio profile.",
                "Audio Source", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            BalLog("Set audio source failed: " + res.Message);
            Dlg("Could not set the audio source.\n\n" + res.Message,
                "Audio Source", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        _srcBtn.Enabled = true;
    }

    // Hard-block dialog used by Apply and Make Custom Firmware when the
    // cached source is USB-C. Returns true if the caller should proceed
    // (either user picked "set and continue" - in which case F702 has been
    // written - or user picked "continue anyway").
    async Task<bool> ConfirmSourceBeforeWrite(string action)
    {
        int state = await EnsureSourceStateAsync();
        if (state != 0x0A) return true;   // wireless or unknown -> proceed

        var r = Dlg(
            $"Your headset is on the USB-C audio profile.\n\n"
            + $"{action} now would tune the wrong profile - the L/R balance\n"
            + "you calibrate may not take effect correctly.\n\n"
            + "Switch to the Wireless profile first?  (Recommended)",
            "Wrong Audio Profile", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
            primaryText: "Set Wireless and continue",
            secondaryText: "Continue anyway");
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes)
        {
            BalLog("Setting audio source to Wireless before continuing...");
            var w = await Task.Run(() => HidRace.WriteSourceState(0x00));
            if (!w.Ok)
            {
                BalLog("Could not set audio source: " + w.Message);
                Dlg("Could not set the audio source.\n\n" + w.Message + "\n\nAbort.",
                    "Audio Source", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            _sourceStateCache = w.State;
            BalLog($"Audio source set to Wireless (0xF702 = 0x{w.State:X2}).");
        }
        return true;   // Yes (set+continue) or No (continue anyway)
    }

    async void OnApply(object sender, EventArgs e)
    {
        if (_flashing || _resetting)
        {
            BalLog("A flash or reset is in progress - wait for it to finish.");
            return;
        }
        if (!await ConfirmSourceBeforeWrite("Applying balance")) return;
        int l = (int)_balL.Value, r = (int)_balR.Value;
        _applyBtn.Enabled = false;
        var res = await Task.Run(() => HidRace.WriteBalance(l, r));
        BalLog((res.Ok ? "Applied: " : "Apply failed: ") + res.Message);
        if (res.Ok)
        {
            _balCurrent.Text = $"Balance:  L {l}   R {r}";
            _lastApplyTick = Environment.TickCount64;
        }
        _applyBtn.Enabled = true;
    }

    // Pop a custom dialog asking which firmware base version to build the
    // custom on. Returns "56" / "61" / "63" / "74", or null on cancel.
    string PickFirmwareVersion()
    {
        using var form = new Form
        {
            Text = "Custom Firmware - choose base version",
            Size = new Size(480, 320),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = BG,
            ForeColor = FG,
        };
        var head = new Label
        {
            Text = "Which Maxwell firmware version do you want to bake your balance into?",
            Location = new Point(20, 18),
            Size = new Size(440, 36),
            ForeColor = FG,
        };
        // Version radio buttons (one per supported version).
        // Order: newest first. v74 is the default (current behavior preserved).
        string[] versions = { "74", "63", "61", "56" };
        string[] descriptions =
        {
            "v1.0.1.74  -  Latest. All bug fixes; recommended for most users.",
            "v1.0.1.63  -  Older. Has the (incomplete) per-source switching system.",
            "v1.0.1.61  -  First version with the per-source switching system.",
            "v1.0.1.56  -  Pre-feature baseline. No per-source machinery at all - the simplest possible firmware (the L/R imbalance bug doesn't exist in this version).",
        };
        var radios = new RadioButton[versions.Length];
        int y = 60;
        for (int i = 0; i < versions.Length; i++)
        {
            radios[i] = new RadioButton
            {
                Text = descriptions[i],
                Location = new Point(28, y),
                Size = new Size(420, 40),
                ForeColor = FG,
                BackColor = BG,
                Checked = versions[i] == _lastFwVersion,
            };
            y += 44;
        }
        if (!radios.Any(r => r.Checked)) radios[0].Checked = true;
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(280, 240),
            Size = new Size(80, 30),
            BackColor = ACCENT,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(370, 240),
            Size = new Size(80, 30),
            BackColor = PANEL,
            ForeColor = FG,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderSize = 0;
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Controls.Add(head);
        foreach (var rb in radios) form.Controls.Add(rb);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        if (form.ShowDialog(this) != DialogResult.OK) return null;
        for (int i = 0; i < radios.Length; i++)
            if (radios[i].Checked) { _lastFwVersion = versions[i]; return versions[i]; }
        return null;
    }

    async void OnMakeFirmware(object sender, EventArgs e)
    {
        if (_flashing || _resetting)
        {
            BalLog("A flash or reset is in progress - wait for it to finish.");
            return;
        }
        if (!await ConfirmSourceBeforeWrite("Building custom firmware")) return;
        int l = (int)_balL.Value, r = (int)_balR.Value;

        // Pick Xbox vs PlayStation from the connected headset; fall back to the
        // last detected platform if no headset is connected right now.
        string variant = await Task.Run(() => HidRace.DetectVariant());
        bool ps = variant == "PlayStation" || (variant == null && _isPs);
        string platName = ps ? "PlayStation" : "Xbox";
        string srcNote = variant != null ? "from the connected headset" : "from the last detected device";

        // Pick which firmware base version to build on. Returns null if user
        // cancels the picker dialog.
        string version = PickFirmwareVersion();
        if (version == null) return;
        string verStr = $"v1.0.1.{version}";

        // v56 has only ONE balance key (no per-source switching exists yet),
        // so the confirmation text differs slightly.
        string v56Note = version == "56"
            ? "\n\nNOTE: v1.0.1.56 predates the per-source switching system. "
              + "It has a single balance default (the bug introduced in v1.0.1.61 "
              + "doesn't exist in this version). Your calibrated balance will be "
              + "written to the one NVDM key v56 uses."
            : "";

        if (Dlg(
            $"Build a custom {platName} {verStr} firmware with balance L={l}, R={r}?\n\n"
            + $"Platform: {platName} ({srcNote}).\n"
            + $"Base: stock {verStr}." + v56Note + "\n\n"
            + "It will be saved and added to the Firmware tab to flash.",
            "Make Custom Firmware", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        _makeFwBtn.Enabled = false;
        BalLog($"Building custom {verStr} firmware...");
        try
        {
            byte[] fw = await Task.Run(() => FirmwarePatcher.Build(ps, version, l, r));
            Directory.CreateDirectory(CustomDir);
            string tag = ps ? "PS" : "XBOX";
            string path = Path.Combine(CustomDir, $"{tag}_{version}_balance_L{l}_R{r}.bin");
            File.WriteAllBytes(path, fw);
            BalLog($"Built custom {platName} {verStr} firmware: {Path.GetFileName(path)} ({fw.Length} bytes)");
            RefreshVersions();
            string disp = "Custom " + Path.GetFileNameWithoutExtension(path);
            int idx = _version.Items.IndexOf(disp);
            if (idx >= 0) _version.SelectedIndex = idx;
            ShowPage(true);
            Dlg(
                $"Custom {platName} {verStr} firmware created and selected on the Firmware tab.\n\n"
                + "1. Click Flash Firmware to flash it.\n"
                + "   If the headset is already on the same version, flash a\n"
                + "   different stock version first - you cannot reflash the\n"
                + "   same version.\n"
                + "2. After flashing, the tool walks you through the\n"
                + "   required factory reset.",
                "Custom Firmware Ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            BalLog("Build failed: " + ex.Message);
            Dlg("Could not build firmware:\n" + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        _makeFwBtn.Enabled = true;
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            string dir = AppContext.BaseDirectory;
            var lines = new List<string>();
            bool anyFail = false;
            foreach (string version in FirmwarePatcher.SupportedVersions)
            {
                foreach (bool ps in new[] { false, true })
                {
                    string tag = ps ? "ps" : "xbox";
                    string outFile = Path.Combine(dir, $"selftest_{tag}_{version}.bin");
                    try
                    {
                        byte[] fw = FirmwarePatcher.Build(ps, version, 143, 148);
                        File.WriteAllBytes(outFile, fw);
                        lines.Add($"OK  {tag} v{version}: {fw.Length} bytes");
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"FAIL {tag} v{version}: {ex.Message}");
                        anyFail = true;
                    }
                }
            }
            string summary = (anyFail ? "FAIL\n" : "OK\n") + string.Join("\n", lines);
            File.WriteAllText(Path.Combine(dir, "selftest_result.txt"), summary);
            return;
        }

        // Hidden test helper: write any byte to NVDM 0xF702. Used to force the
        // headset into the "wrong" audio profile for QA of the Set Audio Source
        // button on the Balance tab. Usage:
        //   MaxwellTool.exe --setsource 0x0A   (force USB-C profile)
        //   MaxwellTool.exe --setsource 0      (force wireless profile)
        if (args.Length >= 2 && args[0] == "--setsource")
        {
            string s = args[1];
            byte val;
            try { val = (byte)Convert.ToInt32(s, s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10); }
            catch { Console.WriteLine("usage: MaxwellTool.exe --setsource <byte>  (e.g. 0x0A or 0)"); return; }
            Console.WriteLine($"Reading current source state...");
            var pre = HidRace.ReadSourceState();
            Console.WriteLine(pre.Ok ? $"  before: 0xF702 = 0x{pre.State:X2}" : $"  read failed: {pre.Message}");
            Console.WriteLine($"Writing 0xF702 = 0x{val:X2}...");
            var w = HidRace.WriteSourceState(val);
            if (w.Ok) Console.WriteLine($"  OK. 0xF702 is now 0x{w.State:X2}.");
            else Console.WriteLine($"  write failed: {w.Message}");
            return;
        }

        AirohaSDK.SetupDllPath();

        // The native SDK DLLs must sit next to the exe. If the download was
        // extracted wrong, or the exe was copied out on its own, fail with a
        // clear message instead of a raw DllNotFoundException crash later on.
        string[] needed =
        {
            "AirohaHidCoreLib.dll", "AirohaPeqLibrary.dll",
            "libgcc_s_seh-1.dll", "libstdc++-6.dll", "libwinpthread-1.dll",
        };
        var missing = needed.Where(d => !File.Exists(Path.Combine(AppContext.BaseDirectory, d))).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(
                "Maxwell Tool cannot start - required files are missing:\n\n  "
                + string.Join("\n  ", missing) + "\n\n"
                + "Keep MaxwellTool.exe in the folder it came with: it needs its\n"
                + "DLLs (and the firmware folder) right next to it. Re-extract the\n"
                + "download and run MaxwellTool.exe from inside that folder.",
                "Maxwell Tool - Missing Files", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Catch-all so an unexpected error shows a readable message instead of
        // the raw .NET crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);
        Application.Run(new MaxwellForm());
    }

    static void ShowFatal(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "Maxwell Tool hit an unexpected error:\n\n"
                + (ex?.Message ?? "unknown error") + "\n\n"
                + "Make sure MaxwellTool.exe is in its original folder with all its\n"
                + "DLLs, the headset is connected, and the Audeze app is closed.",
                "Maxwell Tool - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
