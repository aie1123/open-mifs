// =====================================================================
//  Ui.cs —— 界面 token 与自绘控件（v0.6.0「仪表台」重构）
//  ---------------------------------------------------------------------
//  设计依据：docs/UI-DESIGN.md（v2）
//    · token 唯一来源：颜色 / 字体 / 间距 全部从这里取，主窗体不再写死色值
//    · 表面语义：凹陷方角 = 只读读数；平面圆角 4 = 可交互
//    · 档位只用颜色表达（用户明确不要「凉/温/热/烫」文字）；档位色 = 托盘图标同一套阈值
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

        // ── 缩放工具：布局全部按"设计单位 × s"计算，s 由窗口大小决定
        private static readonly System.Collections.Generic.Dictionary<string, Font> _fontCache =
            new System.Collections.Generic.Dictionary<string, Font>();

        /// <summary>按缩放系数取字体（带缓存）。mono=true 用 Consolas（数值）。</summary>
        public static Font F(float pt, bool bold, bool mono, double s)
        {
            float size = (float)(pt * s);
            if (size < 5F) size = 5F;
            if (size > 40F) size = 40F;
            size = (float)(Math.Round(size * 4.0) / 4.0);   // 量化到 0.25pt：拖动时缓存不会爆、GDI 句柄不churn
            string key = (mono ? "m" : "y") + (bold ? "b" : "r") + size.ToString("0.##", CultureInfo.InvariantCulture);
            Font f;
            if (_fontCache.TryGetValue(key, out f)) return f;
            string family = mono ? "Consolas" : "Microsoft YaHei UI";
            try { f = new Font(family, size, bold ? FontStyle.Bold : FontStyle.Regular); }
            catch { f = new Font(FontFamily.GenericSansSerif, size, bold ? FontStyle.Bold : FontStyle.Regular); }
            _fontCache[key] = f;
            return f;
        }

        /// <summary>设计单位 → 像素。</summary>
        public static int S(double v, double s) { return (int)Math.Round(v * s); }

        /// <summary>标签高度：按字体实际行高给，避免中文下缘被截断。</summary>
        public static int TextH(Font f, double s) { return Math.Max(f.Height + Math.Max(3, S(4, s)), S(16, s)); }

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

        // ── 档位颜色可自定义（全局一套；settings.txt: color_l1..color_l4 = #RRGGBB）
        private static Color[] _levels = new Color[] { Cold, Warm, Hot, Scald };
        public static readonly Color[] LevelPresets = new Color[] { Cold, Warm, Hot, Scald };

        /// <summary>档位原色（图标填充、色块用）。level 1~4。</summary>
        public static Color LevelColor(int level)
        {
            int i = level <= 1 ? 0 : (level >= 4 ? 3 : level - 1);
            return _levels[i];
        }

        /// <summary>面板文字色：原色对比度不足 4.5:1 时自动加深（保持色相），保证可读。</summary>
        public static Color PanelColorOf(int level)
        {
            Color c = LevelColor(level);
            if (RawColors) return c;          // 用户选择"用原色"时不做任何加深
            int guard = 0;
            while (Contrast(c, Recessed) < 4.5 && guard++ < 24)
                c = Color.FromArgb((int)(c.R * 0.86), (int)(c.G * 0.86), (int)(c.B * 0.86));
            return c;
        }

        /// <summary>相对亮度（WCAG）。</summary>
        public static double RelLum(Color c)
        {
            // 注意：C# 5 不支持局部函数，所以这里不抽 f()
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
            g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
            b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        /// <summary>两色对比度。</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = RelLum(a) + 0.05, lb = RelLum(b) + 0.05;
            return la > lb ? la / lb : lb / la;
        }

        /// <summary>托盘图标上的字符色：填充色偏亮时用深字，否则白字。</summary>
        public static Color IconTextColor(Color fill)
        {
            return RelLum(fill) > 0.55 ? Ink : Color.White;
        }

        public static string HexOf(Color c)
        {
            return "#" + c.R.ToString("X2", CultureInfo.InvariantCulture)
                       + c.G.ToString("X2", CultureInfo.InvariantCulture)
                       + c.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        /// <summary>解析 "#RRGGBB" / "RRGGBB"（大小写皆可）。</summary>
        public static bool TryParseHex(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            int r, g, b;
            if (!int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)) return false;
            if (!int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) return false;
            if (!int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return false;
            c = Color.FromArgb(r, g, b);
            return true;
        }

        /// <summary>启动时载入档位颜色（缺键/非法值一律回退默认并留日志）。</summary>
        public static void LoadColors()
        {
            Color[] def = new Color[] { Cold, Warm, Hot, Scald };
            for (int i = 0; i < 4; i++)
            {
                string raw = Settings.Get("color_l" + (i + 1).ToString(CultureInfo.InvariantCulture), "");
                Color c;
                if (raw.Length == 0) { _levels[i] = def[i]; continue; }
                if (TryParseHex(raw, out c)) _levels[i] = c;
                else { _levels[i] = def[i]; Log.Warn("档位颜色非法，回退默认：" + raw + " → " + HexOf(def[i])); }
            }
            Log.Info("档位颜色：" + HexOf(_levels[0]) + " / " + HexOf(_levels[1]) + " / " + HexOf(_levels[2]) + " / " + HexOf(_levels[3]));
            RawColors = Settings.Get("color_raw", "0") == "1";
            Log.Info("面板数值" + (RawColors ? "使用原色" : "使用可读色（自动加深）"));
        }

        /// <summary>true = 面板数值直接用配置原色（不做对比度加深）。默认 false（保证可读）。</summary>
        public static bool RawColors = false;

        /// <summary>设置"面板数值用原色"并落盘。</summary>
        public static void SetRawColors(bool on)
        {
            RawColors = on;
            try { Settings.Set("color_raw", on ? "1" : "0"); } catch (Exception ex) { Log.Ex("保存原色开关失败", ex); }
            Log.Info("面板数值" + (on ? "使用原色（不做对比度加深）" : "使用可读色（自动加深）"));
        }

        /// <summary>四个档位颜色全部恢复默认。</summary>
        public static void ResetColors()
        {
            Color[] def = new Color[] { Cold, Warm, Hot, Scald };
            for (int i = 0; i < 4; i++)
            {
                _levels[i] = def[i];
                try { Settings.Set("color_l" + (i + 1).ToString(CultureInfo.InvariantCulture), HexOf(def[i])); }
                catch (Exception ex) { Log.Ex("恢复默认颜色失败", ex); }
            }
            Log.Info("档位颜色已恢复默认：" + HexOf(def[0]) + " / " + HexOf(def[1]) + " / " + HexOf(def[2]) + " / " + HexOf(def[3]));
        }

        /// <summary>保存某一档颜色（level 1~4）。</summary>
        public static void SaveColor(int level, Color c)
        {
            int i = level <= 1 ? 0 : (level >= 4 ? 3 : level - 1);
            _levels[i] = c;
            try { Settings.Set("color_l" + (i + 1).ToString(CultureInfo.InvariantCulture), HexOf(c)); }
            catch (Exception ex) { Log.Ex("保存档位颜色失败", ex); }
            Log.Info("档位颜色 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " → " + HexOf(c));
        }

        /// <summary>档位字（色块标签用）。</summary>
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
            s = s.Replace(".0 GB", " GB").Replace(".0 %", " %").Replace(".0 GHz", " GHz")
                 .Replace(".0 W", " W").Replace(".0 RPM", " RPM");
            while (s.IndexOf("  ") >= 0) s = s.Replace("  ", " ");   // 连续空格收成一个（省宽度）
            return s;
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
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Height - 1), Ui.Ink,
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
        private Font _valFont;          // 常规值字体（10pt 等宽）
        private Font _valFontSmall;     // 值太长时降一档（9pt 等宽），保证不被裁掉
        private string _rawValue = "";
        private string _rawUnit = "";
        private bool _rawOk = true;
        private int _rawLevel;
        private bool _hasValue;

        public ReadoutRow(Control host, string key)
        {
            _key.AutoSize = false;
            _key.Text = key;
            _key.ForeColor = Ui.Label;
            _key.BackColor = Color.Transparent;
            _key.TextAlign = ContentAlignment.MiddleRight;
            host.Controls.Add(_key);

            _val.AutoSize = false;
            _val.ForeColor = Ui.Ink;
            _val.BackColor = Color.Transparent;
            _val.TextAlign = ContentAlignment.MiddleRight;
            host.Controls.Add(_val);

            _unit.AutoSize = false;
            _unit.ForeColor = Ui.Label;
            _unit.BackColor = Color.Transparent;
            _unit.TextAlign = ContentAlignment.MiddleLeft;
            host.Controls.Add(_unit);
        }

        /// <summary>按当前缩放重排（Resize 时调用）。</summary>
        public void Layout(int y, int rowW, int rowH, double s, Font keyFont, Font valFont, Font valFontSmall, Font unitFont)
        {
            _valFont = valFont; _valFontSmall = valFontSmall;
            int labelW = Ui.S(Ui.LabelW, s), unitW = Ui.S(Ui.UnitW, s), gap = Ui.S(Ui.Gap, s);
            int h = Ui.TextH(keyFont, s);
            _key.Font = keyFont;
            _key.Location = new Point(0, y + (rowH - h) / 2);
            _key.Size = new Size(labelW, h);
            int valW = rowW - labelW - gap - unitW;
            if (valW < Ui.S(40, s)) valW = Ui.S(40, s);
            _val.Font = valFont;
            _val.Location = new Point(labelW + gap, y);
            _val.Size = new Size(valW, rowH);
            _unit.Font = unitFont;
            _unit.Location = new Point(rowW - unitW, y + (rowH - h) / 2);
            _unit.Size = new Size(unitW, h);
            FitFont();      // 关键：重排后按新列宽重新决定字号（旧版在这里被重置回大字，导致拖动后长值被裁）
        }

        public Label ValueLabel { get { return _val; } }

        /// <summary>整行可见性（多风扇/多硬盘时用：没有这一路就隐藏，不留空行）。</summary>
        public bool Visible
        {
            get { return _key.Visible; }
            set { _key.Visible = value; _val.Visible = value; _unit.Visible = value; }
        }

        /// <summary>ok=false → 数值走 Muted（不可用）；level&gt;0 → 数值走档位色。</summary>
        public void Set(string value, string unit, bool ok, int level)
        {
            _rawValue = value; _rawUnit = unit; _rawOk = ok; _rawLevel = level; _hasValue = true;
            Apply();
        }

        /// <summary>把记住的原始值重新渲染一遍（含"按当前列宽选字号"）。</summary>
        private void Apply()
        {
            if (!_hasValue) return;
            _val.Text = _rawValue;
            _val.ForeColor = !_rawOk ? Ui.Muted : (_rawLevel > 0 ? Ui.PanelColorOf(_rawLevel) : Ui.Ink);
            _unit.Text = _rawUnit;
            _unit.ForeColor = _rawOk ? Ui.Label : Ui.Muted;
            FitFont();
        }

        /// <summary>值超出列宽就降一档字号（宁可小一点，也不要被裁掉半截）。</summary>
        private void FitFont()
        {
            if (_valFont == null || _val.Width <= 0) return;
            Font f = _valFont;
            if (_valFontSmall != null)
            {
                int need = TextRenderer.MeasureText(_rawValue, _valFont,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (need > _val.Width - Ui.S(4, 1)) f = _valFontSmall;
            }
            if (!object.ReferenceEquals(_val.Font, f)) _val.Font = f;
        }

        /// <summary>拖动窗口时只更新水平尺寸（不动纵向位置与字体档位选择逻辑），并重跑字号适配。</summary>
        public void RefitWidth(int rowW, double s)
        {
            int labelW = Ui.S(Ui.LabelW, s), unitW = Ui.S(Ui.UnitW, s), gap = Ui.S(Ui.Gap, s);
            _key.Width = labelW;
            _val.Left = labelW + gap;
            int valW = rowW - labelW - gap - unitW;
            if (valW < Ui.S(40, s)) valW = Ui.S(40, s);
            _val.Width = valW;
            _unit.Left = rowW - unitW;
            FitFont();
        }

        public void Set(string raw, bool ok, int level)
        {
            string v, u;
            Ui.SplitUnit(raw, out v, out u);
            Set(v, u, ok, level);      // 档位只用颜色表达（用户要求不显示文字档位）
        }
    }

    /// <summary>关键读数（首屏三大数字）：17pt 等宽数值 + 单位 + 标签 + 档位字。</summary>
    internal sealed class BigReadout
    {
        private readonly Label _val = new Label();
        private readonly Label _unit = new Label();
        private readonly Label _cap = new Label();
        private readonly Label _sub = new Label();   // 副读数（如风扇2），与主值同列右对齐
        private int _x, _w;

        public BigReadout(Control host)
        {
            _val.AutoSize = false;
            _val.ForeColor = Ui.Ink;
            _val.BackColor = Color.Transparent;
            _val.TextAlign = ContentAlignment.BottomRight;
            host.Controls.Add(_val);

            _unit.AutoSize = false;
            _unit.ForeColor = Ui.Label;
            _unit.BackColor = Color.Transparent;
            _unit.TextAlign = ContentAlignment.BottomLeft;
            host.Controls.Add(_unit);

            _cap.AutoSize = false;
            _cap.ForeColor = Ui.Label;
            _cap.BackColor = Color.Transparent;
            _cap.TextAlign = ContentAlignment.TopRight;
            host.Controls.Add(_cap);

            _sub.AutoSize = false;
            _sub.ForeColor = Ui.Label;
            _sub.BackColor = Color.Transparent;
            _sub.TextAlign = ContentAlignment.MiddleRight;
            host.Controls.Add(_sub);
        }

        /// <summary>副读数（可为空字符串表示不显示）。</summary>
        public void Sub(string text) { _sub.Text = text; }

        /// <summary>按当前缩放重排（Resize 时调用）。</summary>
        public void Layout(int x, int y, int w, double s, Font valFont, Font unitFont, Font capFont, Font subFont)
        {
            _x = x; _w = w;
            // 单位列宽按文字实际宽度实测（NoPadding 去掉多余空隙，"RPM" 才不会被裁成 "R"）
            int unitW = TextRenderer.MeasureText("RPM", unitFont,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.S(4, s);
            if (unitW < Ui.S(24, s)) unitW = Ui.S(24, s);
            int valH = Math.Max(valFont.Height, Ui.S(22, s));
            _val.Font = valFont;
            _val.Location = new Point(x, y);
            _val.Size = new Size(w - unitW, valH);
            _unit.Font = unitFont;
            _unit.Location = new Point(x + w - unitW, y + valH - unitFont.Height - Ui.S(2, s));
            _unit.Size = new Size(unitW, unitFont.Height + Ui.S(4, s));
            _cap.Font = capFont;
            _cap.Location = new Point(x, y + valH + Ui.S(2, s));
            _cap.Size = new Size(w, Ui.TextH(capFont, s));
            _sub.Font = subFont;
            _sub.Location = new Point(x, y + valH + Ui.S(2, s) + Ui.TextH(capFont, s));
            _sub.Size = new Size(w, Ui.TextH(subFont, s));
        }

        /// <summary>value 为空表示暂无数据（显示 —）。level&gt;0 时数值走档位色。</summary>
        public void Set(string value, string unit, string caption, int level, bool ok)
        {
            bool has = ok && !string.IsNullOrEmpty(value);
            _val.Text = has ? value : "—";
            _val.ForeColor = !has ? Ui.Muted : (level > 0 ? Ui.PanelColorOf(level) : Ui.Ink);
            _unit.Text = has ? unit : "";
            _cap.Text = caption;      // 不再拼"· 凉/温/热/烫"（用户要求去掉）
            _cap.ForeColor = (!has || level == 0) ? Ui.Label : Ui.PanelColorOf(level);
        }
    }
}
