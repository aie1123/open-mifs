// =====================================================================
//  Ui.cs —— 界面 token 与自绘控件（v0.6.0「仪表台」重构）
//  ---------------------------------------------------------------------
//  设计依据：docs/UI-DESIGN.md（v2）
//    · token 唯一来源：颜色 / 字体 / 间距 全部从这里取，主窗体不再写死色值
//    · 表面语义：凹陷方角 = 只读读数；平面圆角 4 = 可交互
//    · 状态永不只靠颜色：档位同时给「凉/温/热/烫」文字（ui-ux-pro-max: Color Only, High）
//    · 键盘可达：自绘控件带 2px 焦点环（Ring）；Tab 顺序由调用方给定
//  语法约束：.NET Framework 自带 csc 仅支持 C# 5（无字符串插值 / ?. / nameof）
// =====================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace OpenMIFS
{
    /// <summary>设计 token 的唯一来源。</summary>
    internal static class Ui
    {
        // ── 基础色（对比度见 docs/UI-DESIGN.md §1.1，均已实测复算）
        public static readonly Color Surface = Color.FromArgb(0xF5, 0xF5, 0xF4);
        public static readonly Color Recessed = Color.White;
        public static readonly Color Hairline = Color.FromArgb(0xE2, 0xDF, 0xD8);
        public static readonly Color Ink = Color.FromArgb(0x1B, 0x1B, 0x1E);
        public static readonly Color Label = Color.FromArgb(0x6B, 0x6B, 0x73);
        public static readonly Color Muted = Color.FromArgb(0xA1, 0xA1, 0xAA);
        public static readonly Color Ring = Color.FromArgb(0x1B, 0x1B, 0x1E);
        public static readonly Color Destructive = Color.FromArgb(0xA8, 0x24, 0x24);
        public static readonly Color HoverFill = Color.FromArgb(0xEF, 0xEF, 0xEC);
        public static readonly Color DownFill = Color.FromArgb(0xE4, 0xE4, 0xE1);
        public static readonly Color DisabledFill = Color.FromArgb(0xEC, 0xEC, 0xEA);

        // ── 语义四档（跨托盘图标 / 面板 / OSD 守恒）
        public static readonly Color Cold = Color.FromArgb(0x26, 0x76, 0x42);
        public static readonly Color Warm = Color.FromArgb(0x8A, 0x65, 0x10);
        public static readonly Color Hot = Color.FromArgb(0xB0, 0x4A, 0x18);
        public static readonly Color Scald = Color.FromArgb(0xA8, 0x24, 0x24);

        // ── 字体（4 档，见 §1.3）
        public static readonly Font FontTitle = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        public static readonly Font FontUi = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font FontSmall = new Font("Microsoft YaHei UI", 8F);
        public static readonly Font FontSmallBold = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
        public static readonly Font FontBig = new Font("Consolas", 17F, FontStyle.Bold);   // 关键读数
        public static readonly Font FontValue = new Font("Consolas", 11F, FontStyle.Bold); // 明细读数

        // ── 度量（4px 基）
        public const int Pad = 12;
        public const int Gap = 8;
        public const int RowH = 24;       // 读数行高（11pt × 1.5 ≈ 24）
        public const int CtrlH = 28;      // 控件行高
        public const int LeftW = 456;     // 左列固定宽
        public const int LabelW = 92;     // 标签列宽（右对齐）
        public const int UnitW = 76;      // 单位列宽（左对齐）
        public const int StatusBarH = 40;

        /// <summary>温度档位（阈值与托盘图标共用一套，见 TrayIcon.ThresholdsOf）。</summary>
        public static int LevelFor(double celsius)
        {
            double[] t = TrayIcon.ThresholdsOf("cput");
            int lv = 1;
            if (celsius >= t[0]) lv = 2;
            if (celsius >= t[1]) lv = 3;
            if (celsius >= t[2]) lv = 4;
            return lv;
        }

        public static Color LevelColor(int level)
        {
            if (level <= 1) return Cold;
            if (level == 2) return Warm;
            if (level == 3) return Hot;
            return Scald;
        }

        /// <summary>档位字：颜色之外的第二通道（Color Only 规则的落法）。</summary>
        public static string LevelWord(int level)
        {
            if (level <= 1) return "凉";
            if (level == 2) return "温";
            if (level == 3) return "热";
            return "烫";
        }

        /// <summary>把 "23.96 W" / "55 ℃" 拆成数值与单位；拆不开就整体当数值。</summary>
        public static void SplitUnit(string raw, out string value, out string unit)
        {
            value = raw == null ? "" : raw.Trim();
            unit = "";
            if (value.Length == 0) return;
            // 复合值（含 / 或（ 的，例如内存占用）不拆
            if (value.IndexOf('/') >= 0 || value.IndexOf('（') >= 0) return;
            int sp = value.LastIndexOf(' ');
            if (sp <= 0 || sp >= value.Length - 1) return;
            string tail = value.Substring(sp + 1);
            if (tail.Length == 0 || tail.Length > 4) return;
            if (tail == "℃" || tail == "W" || tail == "GHz" || tail == "MHz" || tail == "RPM" || tail == "%" || tail == "GB" || tail == "V")
            {
                unit = tail;
                value = value.Substring(0, sp).Trim();
            }
        }

        /// <summary>去掉多余的 .0（Number Formatting：32.0 GB → 32 GB，100.0 % → 100 %）。</summary>
        public static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace(".0 GB", " GB").Replace(".0 %", " %").Replace(".0 GHz", " GHz")
                    .Replace(".0 W", " W").Replace(".0 RPM", " RPM");
        }

        /// <summary>供电类型 → 人话（0 电池 / 1 Type-C / 2 圆口）。</summary>
        public static string AcText(int v)
        {
            if (v == 0) return "电池供电";
            if (v == 1) return "Type-C 供电";
            if (v == 2) return "圆口 DC 供电";
            return "供电未知";
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (rad <= 0) { p.AddRectangle(r); return p; }
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>细线分隔条（1px，占位 1px）。</summary>
        public static Panel Hairline_(int x, int y, int w, Control host)
        {
            Panel p = new Panel();
            p.BackColor = Hairline;
            p.Location = new Point(x, y);
            p.Size = new Size(w, 1);
            p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            host.Controls.Add(p);
            return p;
        }
    }

    // ───────────────────────────────────────────────────────────── 交互控件
    /// <summary>平面圆角按钮（圆角 4）。选中态 = 墨块白字（实体拨杆的观感）；键盘焦点有 2px 焦点环。</summary>
    internal class FlatButton : Control
    {
        private bool _hover, _down, _selected, _restricted;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw | ControlStyles.Selectable
                   | ControlStyles.SupportsTransparentBackColor, true);   // 下面要设 Transparent，必须先开这个
            TabStop = true;
            Font = Ui.FontUi;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(120, Ui.CtrlH);
        }

        /// <summary>选中态（性能模式的当前档、背光的当前等级、已开启的开关）。</summary>
        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        /// <summary>受限态（功能被环境禁用，但控件本身可能仍可点）——文字用琥珀。</summary>
        public bool Restricted
        {
            get { return _restricted; }
            set { if (_restricted != value) { _restricted = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled) { _down = true; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space || keyData == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color fill, text, border;
            if (!Enabled) { fill = Ui.DisabledFill; text = Ui.Muted; border = Ui.Hairline; }
            else if (_selected) { fill = Ui.Ink; text = Color.White; border = Ui.Ink; }
            else if (_down) { fill = Ui.DownFill; text = Ui.Ink; border = Ui.Hairline; }
            else if (_hover) { fill = Ui.HoverFill; text = Ui.Ink; border = Ui.Hairline; }
            else { fill = Ui.Recessed; text = _restricted ? Ui.Warm : Ui.Ink; border = Ui.Hairline; }

            using (GraphicsPath path = Ui.Round(r, 4)) { using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path); }
            using (GraphicsPath path = Ui.Round(r, 4)) { using (Pen pen = new Pen(border)) g.DrawPath(pen, path); }
            if (Focused)
            {
                Rectangle fr = new Rectangle(1, 1, Width - 3, Height - 3);
                using (GraphicsPath path = Ui.Round(fr, 3)) { using (Pen pen = new Pen(Ui.Ring, 2F)) g.DrawPath(pen, path); }
            }
            TextRenderer.DrawText(g, Text, Font, r, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // ───────────────────────────────────────────────────────────── 只读面
    /// <summary>凹陷只读面板：白底、方角、1px 内线（表面语义：凹陷 = 数据，不是控件）。</summary>
    internal sealed class RecessedPanel : Panel
    {
        public RecessedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Recessed;
            Padding = new Padding(Ui.Pad);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Ui.Hairline))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>区块标题：9pt 半粗 + 底部细线（结构即信息，不做装饰框）。</summary>
    internal sealed class SectionTitle : Control
    {
        public SectionTitle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            Font = Ui.FontTitle;
            Height = 26;
            BackColor = Ui.Surface;      // 自绘控件不要用 Transparent（未开 SupportsTransparentBackColor 会抛异常）
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, 18), Ui.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (Pen p = new Pen(Ui.Hairline)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>一行读数：标签（右对齐 92）/ 数值（等宽右对齐）/ 单位（左对齐固定列）。</summary>
    internal sealed class ReadoutRow
    {
        private readonly Label _key = new Label();
        private readonly Label _val = new Label();
        private readonly Label _unit = new Label();

        public ReadoutRow(Control host, int y, string key, int width)
        {
            _key.AutoSize = false;
            _key.Text = key;
            _key.Font = Ui.FontSmall;
            _key.ForeColor = Ui.Label;
            _key.BackColor = Color.Transparent;
            _key.TextAlign = ContentAlignment.MiddleRight;
            _key.Location = new Point(0, y + 2);
            _key.Size = new Size(Ui.LabelW, 18);
            host.Controls.Add(_key);

            int valW = width - Ui.LabelW - Ui.Gap - Ui.UnitW;
            if (valW < 40) valW = 40;
            _val.AutoSize = false;
            _val.Font = Ui.FontValue;
            _val.ForeColor = Ui.Ink;
            _val.BackColor = Color.Transparent;
            _val.TextAlign = ContentAlignment.MiddleRight;
            _val.Location = new Point(Ui.LabelW + Ui.Gap, y);
            _val.Size = new Size(valW, Ui.RowH - 2);
            host.Controls.Add(_val);

            _unit.AutoSize = false;
            _unit.Font = Ui.FontSmall;
            _unit.ForeColor = Ui.Label;
            _unit.BackColor = Color.Transparent;
            _unit.TextAlign = ContentAlignment.MiddleLeft;
            _unit.Location = new Point(width - Ui.UnitW, y + 2);
            _unit.Size = new Size(Ui.UnitW, 18);
            host.Controls.Add(_unit);
        }

        public Label ValueLabel { get { return _val; } }

        /// <summary>ok=false → 数值走 Muted（不可用）；level&gt;0 → 档位色 + 档位字。</summary>
        public void Set(string value, string unit, bool ok, int level)
        {
            _val.Text = value;
            _val.ForeColor = !ok ? Ui.Muted : (level > 0 ? Ui.LevelColor(level) : Ui.Ink);
            _unit.Text = unit;
            _unit.ForeColor = ok ? Ui.Label : Ui.Muted;
        }

        public void Set(string raw, bool ok, int level)
        {
            string v, u;
            Ui.SplitUnit(raw, out v, out u);
            if (level > 0 && u.Length > 0) u = u + " " + Ui.LevelWord(level);
            Set(v, u, ok, level);
        }
    }

    /// <summary>关键读数（首屏三大数字）：17pt 等宽数值 + 单位 + 标签 + 档位字。</summary>
    internal sealed class BigReadout
    {
        private readonly Label _val = new Label();
        private readonly Label _unit = new Label();
        private readonly Label _cap = new Label();
        private readonly int _x, _w;

        public BigReadout(Control host, int x, int y, int w)
        {
            _x = x; _w = w;
            _val.AutoSize = false;
            _val.Font = Ui.FontBig;
            _val.ForeColor = Ui.Ink;
            _val.BackColor = Color.Transparent;
            _val.TextAlign = ContentAlignment.BottomRight;
            _val.Location = new Point(x, y);
            _val.Size = new Size(w - 22, 30);
            host.Controls.Add(_val);

            _unit.AutoSize = false;
            _unit.Font = Ui.FontUi;
            _unit.ForeColor = Ui.Label;
            _unit.BackColor = Color.Transparent;
            _unit.TextAlign = ContentAlignment.BottomLeft;
            _unit.Location = new Point(x + w - 22, y + 8);
            _unit.Size = new Size(22, 22);
            host.Controls.Add(_unit);

            _cap.AutoSize = false;
            _cap.Font = Ui.FontSmall;
            _cap.ForeColor = Ui.Label;
            _cap.BackColor = Color.Transparent;
            _cap.TextAlign = ContentAlignment.TopRight;
            _cap.Location = new Point(x, y + 32);
            _cap.Size = new Size(w, 18);
            host.Controls.Add(_cap);
        }

        /// <summary>value 为空表示暂无数据（显示 —）。level&gt;0 时数值走档位色，标签后加档位字。</summary>
        public void Set(string value, string unit, string caption, int level, bool ok)
        {
            bool has = ok && !string.IsNullOrEmpty(value);
            _val.Text = has ? value : "—";
            _val.ForeColor = !has ? Ui.Muted : (level > 0 ? Ui.LevelColor(level) : Ui.Ink);
            _unit.Text = has ? unit : "";
            _cap.Text = (level > 0 && has) ? caption + " · " + Ui.LevelWord(level) : caption;
            _cap.ForeColor = (!has || level == 0) ? Ui.Label : Ui.LevelColor(level);
        }
    }
}
