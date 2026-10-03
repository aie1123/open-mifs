// OpenMIFS — 同方 MIFS (MiInterface) 控制台 · 托盘版
// =====================================================================
// 目标框架 : .NET Framework 4.8（Windows 10/11 系统自带，无需安装运行时）
// 编译方式 : 见 build/build.ps1（用系统自带 csc.exe，产物为单个 exe）
// 语法约束 : Framework 自带 csc 仅支持 C# 5，故不使用字符串插值 / ?. / nameof
//
// 接口说明见 docs/PROTOCOL.md
//   ACPI 设备 : ACPI\PNP0C14\MIFS
//   WMI 类    : root\wmi:MICommonInterface
//   方法      : MiInterface(InData[32]) -> OutData[30]
//   ACPI GUID : {B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// ─────────────────────────────────────────────────────── 程序集版本信息
[assembly: AssemblyTitle("OpenMIFS")]
[assembly: AssemblyDescription("同方 MIFS (MiInterface) 控制台 — 不依赖官方控制中心")]
[assembly: AssemblyProduct("OpenMIFS")]
[assembly: AssemblyCompany("OpenMIFS contributors")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace OpenMIFS
{
    // ──────────────────────────────────────────────────────────── MIFS 调用层
    internal static class Mifs
    {
        private const string WmiNamespace = "root\\wmi";
        private const string WmiClass     = "MICommonInterface";
        private const string WmiMethod    = "MiInterface";
        private const byte   CmdGet       = 250;
        private const byte   CmdSet       = 251;

        public const int FnPerMode       = 8;
        public const int FnGpuMode       = 9;
        public const int FnKbdType       = 10;
        public const int FnFnLock        = 11;
        public const int FnTpLock        = 12;
        public const int FnFanSpeeds     = 13;
        public const int FnRgbMode       = 16;
        public const int FnRgbColor      = 17;
        public const int FnRgbBright     = 18;
        public const int FnAcType        = 19;
        public const int FnMaxFanSwitch  = 20;
        public const int FnMaxFanSpeed   = 21;
        public const int FnCpuTemp       = 22;
        public const int FnCpuPower      = 23;

        private static ManagementObject _instance;
        private static string _lastError = "";

        public static string LastError { get { return _lastError; } }

        private static ManagementObject GetInstance()
        {
            if (_instance != null) return _instance;
            ManagementObjectSearcher searcher =
                new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM " + WmiClass);
            foreach (ManagementBaseObject o in searcher.Get())
            {
                _instance = (ManagementObject)o;
                break;
            }
            if (_instance == null)
                throw new InvalidOperationException("未找到 " + WmiClass + " 实例，本机 BIOS 可能没有暴露 MIFS 接口");
            return _instance;
        }

        public static bool Available
        {
            get
            {
                try { GetInstance(); return true; }
                catch (Exception ex) { _lastError = ex.Message; return false; }
            }
        }

        public static byte[] Call(byte command, int function, byte[] payload)
        {
            ManagementObject inst = GetInstance();
            byte[] inData = new byte[32];
            inData[1] = command;
            inData[3] = (byte)function;
            if (payload != null)
            {
                int n = Math.Min(payload.Length, 28);
                Array.Copy(payload, 0, inData, 4, n);
            }
            ManagementBaseObject inParams = inst.GetMethodParameters(WmiMethod);
            inParams["InData"] = inData;
            ManagementBaseObject outParams = inst.InvokeMethod(WmiMethod, inParams, null);
            byte[] outData = (byte[])outParams["OutData"];
            if (outData == null) throw new InvalidOperationException("方法返回空数据（OutData 为 null）");
            return outData;
        }

        /// <summary>读取单字节数据；未实现的功能号返回 null。</summary>
        public static int? GetByte(int function)
        {
            try { return Call(CmdGet, function, null)[4]; }
            catch (Exception ex) { _lastError = ex.Message; return null; }
        }

        public static void SetByte(int function, byte value)
        {
            Call(CmdSet, function, new byte[] { value });
        }

        /// <summary>风扇满速开关（参数 [4]=风扇组 0，[5]=0 正常 / 1 满速）。</summary>
        public static void SetFanBoost(byte value)
        {
            Call(CmdSet, FnMaxFanSwitch, new byte[] { 0, value });
        }

        /// <summary>读取三个风扇转速（RPM），失败返回 null。</summary>
        public static int[] GetFans()
        {
            try
            {
                byte[] r = Call(CmdGet, FnFanSpeeds, null);
                return new int[]
                {
                    r[4] + (r[5] << 8),
                    r[6] + (r[7] << 8),
                    r[10] + (r[11] << 8)
                };
            }
            catch (Exception ex) { _lastError = ex.Message; return null; }
        }
    }

    // ──────────────────────────────────────────────────────── 性能模式映射表
    internal static class ModeMap
    {
        // 上游 Linux 驱动 tongfang-mifs-wmi 标注：0=均衡 1=性能 2=低功耗
        // 本机（无界 14 Pro 2023 / R7-7840HS）实测相反：0=性能 1=均衡 2=低功耗
        // 换机型若发现对不上，跑 .\src\mifs.ps1 bench 实测后改这里。
        public static readonly string[] Order = new string[] { "低功耗", "均衡", "性能" };

        public static int Value(string name)
        {
            switch (name)
            {
                case "低功耗": return 2;
                case "均衡":   return 1;
                case "性能":   return 0;
            }
            return -1;
        }

        public static string Label(int v)
        {
            switch (v)
            {
                case 0: return "性能";
                case 1: return "均衡";
                case 2: return "低功耗";
                case 3: return "满速";
            }
            return "未知(" + v.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    // ──────────────────────────────────────────────────────────────── 主窗口
    internal sealed class MainForm : Form
    {
        private static readonly Color ColOk   = Color.FromArgb(22, 128, 61);
        private static readonly Color ColWarn = Color.FromArgb(185, 28, 28);
        private static readonly Color ColDim  = Color.FromArgb(130, 130, 130);

        private readonly Label _lblHeader = new Label();
        private readonly Button[] _btnMode = new Button[3];
        private readonly Label _lblFan = new Label();
        private readonly Button _btnBoost = new Button();
        private readonly CheckBox _chkFn = new CheckBox();
        private readonly CheckBox _chkTp = new CheckBox();
        private readonly Button[] _btnKbd = new Button[4];
        private readonly TextBox _txtStatus = new TextBox();
        private readonly CheckBox _chkAuto = new CheckBox();
        private readonly ComboBox _cmbInterval = new ComboBox();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

        private bool _suppress;
        private bool _fanBoostOn;
        private bool _trayHintShown;
        private bool _reallyClose;

        private readonly Font _fontUi  = new Font("Microsoft YaHei UI", 9F);
        private readonly Font _fontBold = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private readonly Font _fontUi8 = new Font("Microsoft YaHei UI", 8F);

        public event EventHandler StateChanged;

        public MainForm()
        {
            Text = "OpenMIFS — 同方 MIFS 控制台";
            ClientSize = new Size(480, 586);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = _fontUi;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
            _timer.Interval = 3000;
            _timer.Tick += delegate { if (_chkAuto.Checked) RefreshAll(); };
            _timer.Start();

            Shown += delegate { RefreshAll(); };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) HideToTray(); };
            FormClosing += OnFormClosing;
        }

        public void ForceClose()
        {
            _reallyClose = true;
            _timer.Stop();
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_reallyClose) return;
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (!_trayHintShown && StateChanged != null)
            {
                _trayHintShown = true;
                StateChanged(this, EventArgs.Empty);
            }
        }

        private void Restore()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshAll();
        }

        public void ToggleVisible()
        {
            if (Visible && WindowState != FormWindowState.Minimized) HideToTray();
            else Restore();
        }

        // ────────────────────────────────────────────────────── UI 构建
        private GroupBox NewGroup(string text, int y, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(12, y);
            g.Size = new Size(456, h);
            g.Font = _fontUi8;
            Controls.Add(g);
            return g;
        }

        private void BuildUi()
        {
            _lblHeader.Location = new Point(14, 8);
            _lblHeader.Size = new Size(452, 34);
            _lblHeader.Font = _fontUi8;
            _lblHeader.Text = "正在检测接口…";
            _lblHeader.AutoSize = false;
            Controls.Add(_lblHeader);

            // 性能模式
            GroupBox gMode = NewGroup("性能模式", 48, 70);
            int x = 12;
            for (int i = 0; i < ModeMap.Order.Length; i++)
            {
                Button b = new Button();
                b.Text = ModeMap.Order[i];
                b.Location = new Point(x, 26);
                b.Size = new Size(140, 30);
                b.Tag = ModeMap.Order[i];
                b.Font = new Font("Microsoft YaHei UI", 9F);
                b.Click += OnModeClick;
                gMode.Controls.Add(b);
                _btnMode[i] = b;
                x += 148;
            }

            // 风扇
            GroupBox gFan = NewGroup("风扇", 124, 84);
            _lblFan.Location = new Point(12, 22);
            _lblFan.Size = new Size(432, 20);
            _lblFan.Font = _fontUi8;
            _lblFan.Text = "读取中…";
            _lblFan.AutoSize = false;
            gFan.Controls.Add(_lblFan);

            _btnBoost.Location = new Point(12, 46);
            _btnBoost.Size = new Size(180, 28);
            _btnBoost.Font = new Font("Microsoft YaHei UI", 9F);
            _btnBoost.Text = "风扇满速：关";
            _btnBoost.Click += OnFanBoostClick;
            gFan.Controls.Add(_btnBoost);

            // 硬件开关
            GroupBox gSw = NewGroup("硬件开关", 214, 62);
            _chkFn.Text = "Fn 锁";
            _chkFn.Location = new Point(14, 24);
            _chkFn.Size = new Size(150, 22);
            _chkFn.Font = new Font("Microsoft YaHei UI", 9F);
            _chkFn.Click += OnFnLockClick;
            gSw.Controls.Add(_chkFn);

            _chkTp.Text = "触控板锁定";
            _chkTp.Location = new Point(200, 24);
            _chkTp.Size = new Size(190, 22);
            _chkTp.Font = new Font("Microsoft YaHei UI", 9F);
            _chkTp.Click += OnTpLockClick;
            gSw.Controls.Add(_chkTp);

            // 键盘背光
            GroupBox gKbd = NewGroup("键盘背光亮度", 282, 62);
            x = 12;
            for (int i = 0; i < 4; i++)
            {
                Button b = new Button();
                b.Text = i.ToString(CultureInfo.InvariantCulture);
                b.Location = new Point(x, 24);
                b.Size = new Size(62, 28);
                b.Tag = i;
                b.Font = new Font("Microsoft YaHei UI", 9F);
                b.Click += OnKbdClick;
                gKbd.Controls.Add(b);
                _btnKbd[i] = b;
                x += 76;
            }

            // 状态面板
            _txtStatus.Multiline = true;
            _txtStatus.ReadOnly = true;
            _txtStatus.ScrollBars = ScrollBars.Vertical;
            _txtStatus.WordWrap = false;
            _txtStatus.Font = new Font("Consolas", 9F);
            _txtStatus.Location = new Point(12, 352);
            _txtStatus.Size = new Size(456, 184);
            _txtStatus.BackColor = Color.FromArgb(250, 250, 250);
            Controls.Add(_txtStatus);

            // 底部
            _chkAuto.Text = "自动刷新";
            _chkAuto.Location = new Point(14, 548);
            _chkAuto.Size = new Size(92, 22);
            _chkAuto.Checked = true;
            _chkAuto.Font = _fontUi8;
            Controls.Add(_chkAuto);

            _cmbInterval.Location = new Point(110, 547);
            _cmbInterval.Size = new Size(66, 22);
            _cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInterval.Font = _fontUi8;
            _cmbInterval.Items.AddRange(new object[] { "2 秒", "3 秒", "5 秒", "10 秒" });
            _cmbInterval.SelectedIndex = 1;
            _cmbInterval.SelectedIndexChanged += OnIntervalChanged;
            Controls.Add(_cmbInterval);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.Location = new Point(374, 543);
            btnRefresh.Size = new Size(94, 28);
            btnRefresh.Font = new Font("Microsoft YaHei UI", 9F);
            btnRefresh.Click += delegate { RefreshAll(); };
            Controls.Add(btnRefresh);
        }

        private void OnIntervalChanged(object sender, EventArgs e)
        {
            if (_suppress) return;
            int[] secs = new int[] { 2, 3, 5, 10 };
            int idx = _cmbInterval.SelectedIndex;
            if (idx >= 0 && idx < secs.Length) _timer.Interval = secs[idx] * 1000;
        }

        // ────────────────────────────────────────────────────── 交互
        private void OnModeClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            string name = (string)((Button)sender).Tag;
            try
            {
                Mifs.SetByte(Mifs.FnPerMode, (byte)ModeMap.Value(name));
                Thread.Sleep(250);
                RefreshAll();
            }
            catch (Exception ex) { Warn("切换失败：" + ex.Message); }
        }

        private void OnFanBoostClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            byte target = (byte)(_fanBoostOn ? 0 : 1);
            try
            {
                Mifs.SetFanBoost(target);
                Thread.Sleep(400);
                RefreshAll();
            }
            catch (Exception ex) { Warn("设置失败：" + ex.Message); }
        }

        private void OnFnLockClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try { Mifs.SetByte(Mifs.FnFnLock, (byte)(_chkFn.Checked ? 1 : 0)); Thread.Sleep(200); RefreshAll(); }
            catch (Exception ex) { Warn("Fn 锁设置失败：" + ex.Message); }
        }

        private void OnTpLockClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try { Mifs.SetByte(Mifs.FnTpLock, (byte)(_chkTp.Checked ? 1 : 0)); Thread.Sleep(200); RefreshAll(); }
            catch (Exception ex) { Warn("触控板锁设置失败：" + ex.Message); }
        }

        private void OnKbdClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try { Mifs.SetByte(Mifs.FnRgbBright, (byte)(int)((Button)sender).Tag); Thread.Sleep(150); RefreshAll(); }
            catch (Exception ex) { Warn("背光设置失败：" + ex.Message); }
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, "OpenMIFS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ────────────────────────────────────────────────────── 刷新
        public void RefreshAll()
        {
            _suppress = true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool anyOk = false;

            int? pm = Mifs.GetByte(Mifs.FnPerMode);
            if (pm.HasValue)
            {
                anyOk = true;
                int mv = pm.Value;
                for (int i = 0; i < _btnMode.Length; i++)
                {
                    bool cur = ModeMap.Value(ModeMap.Order[i]) == mv;
                    _btnMode[i].Text = (cur ? "\u25CF " : "\u25CB ") + ModeMap.Order[i];
                    _btnMode[i].Font = cur ? _fontBold : _fontUi;
                }
                sb.AppendLine("性能模式     : " + ModeMap.Label(mv));
            }
            else
            {
                for (int i = 0; i < _btnMode.Length; i++)
                {
                    _btnMode[i].Enabled = false;
                    _btnMode[i].Text = ModeMap.Order[i] + "（未实现）";
                }
                sb.AppendLine("性能模式     : 未实现");
            }

            int[] fans = Mifs.GetFans();
            if (fans != null)
            {
                anyOk = true;
                _lblFan.Text = string.Format(CultureInfo.InvariantCulture,
                    "风扇1  {0} RPM      风扇2  {1} RPM{2}",
                    fans[0], fans[1],
                    fans[2] > 0 ? string.Format(CultureInfo.InvariantCulture, "      风扇3  {0} RPM", fans[2]) : "");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "风扇1        : {0} RPM", fans[0]));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "风扇2        : {0} RPM", fans[1]));
            }
            else
            {
                _lblFan.Text = "风扇：本机未实现";
                sb.AppendLine("风扇转速     : 未实现");
            }

            int? mxs = Mifs.GetByte(Mifs.FnMaxFanSwitch);
            if (mxs.HasValue)
            {
                _fanBoostOn = mxs.Value == 1;
                _btnBoost.Text = "风扇满速：" + (_fanBoostOn ? "开" : "关");
                _btnBoost.Font = _fanBoostOn ? _fontBold : _fontUi;
                sb.AppendLine("风扇满速     : " + (_fanBoostOn ? "开" : "关"));
            }
            else
            {
                _btnBoost.Enabled = false;
                _btnBoost.Text = "风扇满速：未实现";
                sb.AppendLine("风扇满速     : 未实现");
            }

            int? fnl = Mifs.GetByte(Mifs.FnFnLock);
            if (fnl.HasValue)
            {
                _chkFn.Checked = fnl.Value == 1;
                sb.AppendLine("Fn 锁        : " + (_chkFn.Checked ? "开" : "关"));
            }
            else { _chkFn.Enabled = false; _chkFn.Text = "Fn 锁（未实现）"; sb.AppendLine("Fn 锁        : 未实现"); }

            int? tpl = Mifs.GetByte(Mifs.FnTpLock);
            if (tpl.HasValue)
            {
                _chkTp.Checked = tpl.Value == 1;
                sb.AppendLine("触控板锁     : " + (_chkTp.Checked ? "已锁定" : "正常"));
            }
            else { _chkTp.Enabled = false; _chkTp.Text = "触控板锁（未实现）"; sb.AppendLine("触控板锁     : 未实现"); }

            int? kbd = Mifs.GetByte(Mifs.FnRgbBright);
            for (int i = 0; i < _btnKbd.Length; i++)
            {
                if (!kbd.HasValue) { _btnKbd[i].Enabled = false; continue; }
                bool cur = kbd.Value == i;
                _btnKbd[i].Font = cur ? _fontBold : _fontUi;
            }
            sb.AppendLine(kbd.HasValue
                ? "键盘背光     : 等级 " + kbd.Value.ToString(CultureInfo.InvariantCulture)
                : "键盘背光     : 未实现");

            int? ac = Mifs.GetByte(Mifs.FnAcType);
            if (ac.HasValue)
            {
                string t = ac.Value == 1 ? "外接电源" : (ac.Value == 0 ? "电池供电" : "原始值 " + ac.Value);
                sb.AppendLine("供电         : " + t);
            }

            int? ct = Mifs.GetByte(Mifs.FnCpuTemp);
            sb.AppendLine("CPU 温度     : " + (ct.HasValue && ct.Value > 0
                ? ct.Value.ToString(CultureInfo.InvariantCulture) + " ℃" : "未实现"));

            int? cp = Mifs.GetByte(Mifs.FnCpuPower);
            sb.AppendLine("CPU 功率     : " + (cp.HasValue && cp.Value > 0
                ? cp.Value.ToString(CultureInfo.InvariantCulture) + " W" : "未实现"));

            if (anyOk)
            {
                _lblHeader.ForeColor = ColOk;
                _lblHeader.Text = "接口正常 · 已提权 · 托盘常驻\r\n" +
                                  "本机未实现：CPU 温度/功率、GPU 模式、RGB 模式与颜色、风扇满速";
            }
            else
            {
                _lblHeader.ForeColor = ColWarn;
                _lblHeader.Text = "接口不可用 · " + Mifs.LastError + "\r\n" +
                                  "请确认以管理员身份运行，且本机 BIOS 暴露了 MIFS 接口";
            }

            sb.AppendLine();
            sb.AppendLine("提示：MIFS 接口不提供电池充电阈值，无法用软件限制充电到 80%。");
            sb.AppendLine("最后刷新     : " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            _txtStatus.Text = sb.ToString();

            _suppress = false;
            if (StateChanged != null) StateChanged(this, EventArgs.Empty);
        }
    }

    // ─────────────────────────────────────────────────────────── 托盘宿主
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly MainForm _form;
        private readonly ToolStripMenuItem[] _miMode = new ToolStripMenuItem[3];
        private readonly ToolStripMenuItem _miFanBoost = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _miStatus = new ToolStripMenuItem();
        private readonly System.Windows.Forms.Timer _trayTimer = new System.Windows.Forms.Timer();
        private bool _suppress;
        private bool _exiting;

        public TrayContext()
        {
            _form = new MainForm();
            _form.StateChanged += delegate { RefreshTray(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);

            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主界面(&O)");
            miShow.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            miShow.Click += delegate { _form.ToggleVisible(); };
            menu.Items.Add(miShow);
            menu.Items.Add(new ToolStripSeparator());

            _miStatus.Enabled = false;
            menu.Items.Add(_miStatus);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miModeRoot = new ToolStripMenuItem("性能模式");
            for (int i = 0; i < ModeMap.Order.Length; i++)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem(ModeMap.Order[i]);
                mi.Tag = ModeMap.Order[i];
                mi.Click += OnModeClick;
                miModeRoot.DropDownItems.Add(mi);
                _miMode[i] = mi;
            }
            menu.Items.Add(miModeRoot);

            _miFanBoost.Text = "风扇满速";
            _miFanBoost.Click += OnFanBoostClick;
            menu.Items.Add(_miFanBoost);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出(&X)");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);

            _tray.ContextMenuStrip = menu;
            _tray.Text = "OpenMIFS — 同方 MIFS 控制台";
            _tray.Visible = true;
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            _tray.DoubleClick += delegate { _form.ToggleVisible(); };
            _tray.BalloonTipTitle = "OpenMIFS 仍在后台运行";
            _tray.BalloonTipText = "程序已最小化到通知区域，双击图标可重新打开。";
            _tray.BalloonTipIcon = ToolTipIcon.Info;

            _trayTimer.Interval = 5000;
            _trayTimer.Tick += delegate { RefreshTray(); };
            _trayTimer.Start();

            if (!Mifs.Available)
            {
                _tray.ShowBalloonTip(5000, "OpenMIFS",
                    "未检测到 MIFS 接口：" + Mifs.LastError, ToolTipIcon.Warning);
            }

            _form.Show();
            RefreshTray();
        }

        private void OnModeClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            string name = (string)((ToolStripMenuItem)sender).Tag;
            try
            {
                Mifs.SetByte(Mifs.FnPerMode, (byte)ModeMap.Value(name));
                Thread.Sleep(250);
                RefreshTray();
                _form.RefreshAll();
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(4000, "OpenMIFS", "切换失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        private void OnFanBoostClick(object sender, EventArgs e)
        {
            if (_suppress) return;
            try
            {
                int? cur = Mifs.GetByte(Mifs.FnMaxFanSwitch);
                byte target = (byte)((cur.HasValue && cur.Value == 1) ? 0 : 1);
                Mifs.SetFanBoost(target);
                Thread.Sleep(400);
                RefreshTray();
                _form.RefreshAll();
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(4000, "OpenMIFS", "设置失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        private void RefreshTray()
        {
            _suppress = true;
            try
            {
                int? pm = Mifs.GetByte(Mifs.FnPerMode);
                if (pm.HasValue)
                {
                    for (int i = 0; i < _miMode.Length; i++)
                        _miMode[i].Checked = ModeMap.Value(ModeMap.Order[i]) == pm.Value;
                    _miStatus.Text = "当前：" + ModeMap.Label(pm.Value);
                }
                else
                {
                    _miStatus.Text = "当前：接口不可用";
                }

                int? mxs = Mifs.GetByte(Mifs.FnMaxFanSwitch);
                _miFanBoost.Checked = mxs.HasValue && mxs.Value == 1;
                _miFanBoost.Enabled = mxs.HasValue;

                int[] fans = Mifs.GetFans();
                string tip = "OpenMIFS";
                if (pm.HasValue) tip += " · " + ModeMap.Label(pm.Value);
                if (fans != null) tip += string.Format(CultureInfo.InvariantCulture, " · 风扇 {0}/{1} RPM", fans[0], fans[1]);
                if (tip.Length > 62) tip = tip.Substring(0, 62);
                _tray.Text = tip;
            }
            catch { }
            _suppress = false;
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _trayTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _form.ForceClose();
            ExitThread();
        }
    }

    // ─────────────────────────────────────────────────────────────── 入口
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Global\\OpenMIFS_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("OpenMIFS 已在运行。\r\n请在任务栏右下角通知区域找到它的图标。",
                        "OpenMIFS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new TrayContext());
            }
        }
    }
}

