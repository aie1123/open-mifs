// 内容决定高度；窗口决定列数；DPI 决定字号。所有超高内容共用一个纵向滚动视口。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace OpenMIFS
{
    internal sealed partial class MainForm
    {
        private TableLayoutPanel _shell, _workspace;
        private FlowLayoutPanel _header, _footer;
        private Panel _viewport, _controls;
        private RecessedPanel _summary;
        private bool _wideLayout;
        private bool _scrollChrome;
        private double _testDpiScale = 0; // 仅由隔离的布局回归程序设置，不保存到用户配置。

        private void BuildResponsiveShell()
        {
            SuspendLayout();
            _shell = new TableLayoutPanel();
            _shell.Dock = DockStyle.Fill;
            _shell.ColumnCount = 1;
            _shell.RowCount = 3;
            _shell.Margin = Padding.Empty;
            _shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(_shell);

            _header = NewFlow();
            _footer = NewFlow();
            _viewport = new Panel();
            _viewport.Dock = DockStyle.Fill;
            _viewport.AutoScroll = true;
            _viewport.Margin = Padding.Empty;
            _viewport.TabIndex = 1;
            _shell.Controls.Add(_header, 0, 0);
            _shell.Controls.Add(_viewport, 0, 1);
            _shell.Controls.Add(_footer, 0, 2);

            _workspace = new TableLayoutPanel();
            _workspace.Margin = Padding.Empty;
            _workspace.Padding = Padding.Empty;
            _workspace.TabIndex = 0;
            _viewport.Controls.Add(_workspace);
            _controls = new Panel();
            _controls.TabIndex = 1;
            _controls.Dock = DockStyle.Top;
            _summary = new RecessedPanel();
            _summary.Dock = DockStyle.Top;
            foreach (BigReadout readout in _big) _summary.Controls.Add(readout.Container);
            _pnlReadout.Dock = DockStyle.Top;
            _pnlReadout.TabIndex = 0;

            Control[] header = new Control[] { _lblBrand, _lblBadgeMode, _lblBadgeAc, _lblBadgeOsd, _btnElevate, _btnRefreshNow };
            foreach (Control child in header) _header.Controls.Add(child);
            Control[] footer = new Control[] { _chkAuto, _cmbInterval, _lblSensorHint };
            foreach (Control child in footer) _footer.Controls.Add(child);
            Control[] controls = new Control[] { _secMode, _secSw, _secKbd, _secFan, _secOsd,
                _chkFn, _chkTp, _btnBoost, _lblModeNote, _lblKbdNote, _lblBoostNote, _lblHintMode,
                _cmbOsdHint, _btnOsdRestart, _btnOsdDiag, _btnOsdDir, _btnRecap, _chkStartup, _btnPrefs, _pnlPrefs };
            foreach (Control child in controls) _controls.Controls.Add(child);
            foreach (Control child in _btnMode) _controls.Controls.Add(child);
            foreach (Control child in _btnKbd) _controls.Controls.Add(child);
            _controls.Controls.Remove(_pnlPrefs);
            _pnlPrefs.Dock = DockStyle.Top;
            _pnlReadout.Controls.Remove(_topLine);
            _topLine.Dispose();
            _topLine = null;
            Controls.Remove(_barLine);
            _barLine.Dispose();
            _barLine = null;
            _pnlPrefs.BorderStyle = BorderStyle.None;
            WatchLayoutText(_header);
            WatchLayoutText(_footer);
            WatchLayoutText(_controls);
            WatchLayoutText(_pnlPrefs);
            WatchLayoutText(_pnlReadout);
            WatchLayoutText(_summary);
            ResumeLayout(true);
            Rectangle work = Screen.FromControl(this).WorkingArea;
            double scale = LayoutScale();
            ClientSize = new Size(Math.Min(Ui.S(1040, scale), Math.Max(480, work.Width - 64)),
                Math.Min(Ui.S(760, scale), Math.Max(360, work.Height - 80)));
        }

        private static FlowLayoutPanel NewFlow()
        {
            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Fill;
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.WrapContents = true;
            flow.Margin = Padding.Empty;
            return flow;
        }

        private void WatchLayoutText(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                child.TextChanged += delegate { QueueLayout(); };
                if (child.HasChildren) WatchLayoutText(child);
            }
        }

        private double LayoutScale()
        {
            if (_testDpiScale > 0) return _testDpiScale;
            using (Graphics graphics = CreateGraphics()) return Math.Max(1.0, graphics.DpiX / 96.0);
        }

        private void LayoutAll(bool force)
        {
            if (_layoutBusy || _shell == null || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            _layoutBusy = true;
            _shell.SuspendLayout();
            _workspace.SuspendLayout();
            _controls.SuspendLayout();
            _pnlPrefs.SuspendLayout();
            _pnlReadout.SuspendLayout();
            try
            {
                _s = LayoutScale();
                double s = _s;
                int pad = Ui.S(16, s), gap = Ui.S(12, s);
                Font body = Ui.F(9F, false, false, s), small = Ui.F(8F, false, false, s);
                Font bold = Ui.F(9F, true, false, s);
                // 极小窗口 / 大字号下，头尾也加入同一滚动视口，不能把内容区挤成 0 像素。
                bool scrollChrome = ClientSize.Height < Ui.S(600, s);
                if (_scrollChrome != scrollChrome)
                {
                    _scrollChrome = scrollChrome;
                    if (scrollChrome)
                    {
                        _viewport.Controls.Add(_header);
                        _viewport.Controls.Add(_footer);
                        _header.Dock = DockStyle.None;
                        _footer.Dock = DockStyle.None;
                    }
                    else
                    {
                        _shell.Controls.Add(_header, 0, 0);
                        _shell.Controls.Add(_footer, 0, 2);
                        _header.Dock = DockStyle.Fill;
                        _footer.Dock = DockStyle.Fill;
                    }
                }
                _header.Padding = new Padding(pad, Ui.S(10, s), pad, Ui.S(6, s));
                _footer.Padding = new Padding(pad, Ui.S(6, s), pad, Ui.S(10, s));
                int chromeW = ClientSize.Width - (scrollChrome ? SystemInformation.VerticalScrollBarWidth : 0);
                int flowW = Math.Max(1, chromeW - pad * 2);
                foreach (Control child in _header.Controls)
                {
                    child.Font = child == _lblBrand ? bold : body;
                    child.Margin = new Padding(0, 0, gap, Ui.S(6, s));
                    Size size = PreferredControl(child, flowW, s);
                    child.Size = size;
                }
                _chkAuto.Font = body;
                _cmbInterval.Font = body;
                _lblSensorHint.Font = small;
                foreach (Control child in _footer.Controls)
                {
                    child.Margin = new Padding(0, 0, gap, Ui.S(3, s));
                    child.Size = PreferredControl(child, flowW, s);
                }
                _header.MaximumSize = new Size(chromeW, 0);
                _footer.MaximumSize = new Size(chromeW, 0);
                _header.MinimumSize = new Size(chromeW, 0);
                _footer.MinimumSize = new Size(chromeW, 0);
                _header.Width = chromeW;
                _footer.Width = chromeW;
                _header.PerformLayout();
                _footer.PerformLayout();

                // 始终预留一条纵向滚动条的宽度，避免滚动条出现后横向溢出或反复抖动。
                int usableW = Math.Max(1, ClientSize.Width - pad * 2 - SystemInformation.VerticalScrollBarWidth);
                bool wide = usableW >= Ui.S(900, s);
                if (_workspace.Controls.Count == 0 || _wideLayout != wide)
                {
                    _wideLayout = wide;
                    _workspace.Controls.Clear();
                    _workspace.ColumnStyles.Clear();
                    _workspace.RowStyles.Clear();
                    _workspace.ColumnCount = wide ? 2 : 1;
                    _workspace.RowCount = wide ? 3 : 4;
                    if (wide)
                    {
                        _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
                        _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
                        _workspace.Controls.Add(_summary, 0, 0);
                        _workspace.SetColumnSpan(_summary, 2);
                        _workspace.Controls.Add(_controls, 0, 1);
                        _workspace.Controls.Add(_pnlReadout, 1, 1);
                        _workspace.Controls.Add(_pnlPrefs, 0, 2);
                        _workspace.SetColumnSpan(_pnlPrefs, 2);
                    }
                    else
                    {
                        _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                        _workspace.Controls.Add(_summary, 0, 0);
                        _workspace.SetColumnSpan(_summary, 1);
                        _workspace.Controls.Add(_controls, 0, 1);
                        _workspace.Controls.Add(_pnlReadout, 0, 2);
                        _workspace.Controls.Add(_pnlPrefs, 0, 3);
                        _workspace.SetColumnSpan(_pnlPrefs, 1);
                    }
                    for (int i = 0; i < _workspace.RowCount; i++) _workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
                }
                int controlW = wide ? (int)(usableW * .42) - gap : usableW;
                int readoutW = wide ? usableW - (int)(usableW * .42) : usableW;
                _controls.Margin = new Padding(0, 0, wide ? gap : 0, 0);
                _pnlReadout.Margin = new Padding(0, 0, 0, wide ? 0 : gap);
                _summary.Margin = new Padding(0, 0, 0, gap);
                _pnlPrefs.Margin = new Padding(0, gap, 0, 0);
                int summaryH = LayoutSummary(usableW, s, body, small);
                int controlH = LayoutControls(controlW, s, body, small, bold);
                int readoutH = LayoutReadout(readoutW, s, body, small);
                _pnlPrefs.Visible = _prefsOpen;
                int prefsH = _prefsOpen ? LayoutPreferences(s, usableW) : 0;
                _pnlPrefs.Size = new Size(usableW, Math.Max(1, prefsH));
                _summary.Size = new Size(usableW, summaryH);
                _controls.Size = new Size(controlW, controlH);
                _pnlReadout.Size = new Size(readoutW, readoutH);
                int contentH;
                if (wide)
                {
                    _workspace.RowStyles[0].Height = summaryH + gap;
                    _workspace.RowStyles[1].Height = Math.Max(controlH, readoutH);
                    _workspace.RowStyles[2].Height = _prefsOpen ? prefsH + gap : 0;
                    contentH = summaryH + gap + Math.Max(controlH, readoutH) + (_prefsOpen ? prefsH + gap : 0);
                }
                else
                {
                    _workspace.RowStyles[0].Height = summaryH + gap;
                    _workspace.RowStyles[1].Height = controlH;
                    _workspace.RowStyles[2].Height = readoutH + gap;
                    _workspace.RowStyles[3].Height = _prefsOpen ? prefsH + gap : 0;
                    contentH = summaryH + gap + controlH + readoutH + gap + (_prefsOpen ? prefsH + gap : 0);
                }
                Point scroll = _viewport.AutoScrollPosition;
                _workspace.Size = new Size(usableW, contentH);
                int headerH = scrollChrome ? _header.Height : 0;
                int footerH = scrollChrome ? _footer.Height : 0;
                _workspace.Location = new Point(pad + scroll.X, headerH + gap + scroll.Y);
                if (scrollChrome)
                {
                    _header.Location = new Point(scroll.X, scroll.Y);
                    _footer.Location = new Point(scroll.X, headerH + gap * 2 + contentH + scroll.Y);
                }
                _viewport.AutoScrollMinSize = new Size(0, headerH + contentH + gap * 2 + footerH);
                _lastLayW = ClientSize.Width;
                _lastLayH = ClientSize.Height;
            }
            finally
            {
                _pnlReadout.ResumeLayout(false);
                _pnlPrefs.ResumeLayout(false);
                _controls.ResumeLayout(false);
                _workspace.ResumeLayout(true);
                _shell.ResumeLayout(true);
                _layoutBusy = false;
            }
        }

        private static Size PreferredControl(Control control, int availableW, double s)
        {
            int horizontal = Ui.S(control is CheckBox ? 28 : 20, s);
            int vertical = Ui.S(control is FlatButton ? 12 : 6, s);
            int width = TextRenderer.MeasureText(control.Text, control.Font).Width + horizontal;
            ComboBox combo = control as ComboBox;
            if (combo != null)
            {
                foreach (object item in combo.Items) width = Math.Max(width, TextRenderer.MeasureText(item.ToString(), combo.Font).Width + Ui.S(36, s));
            }
            if (control is NumericUpDown) width = TextRenderer.MeasureText("888", control.Font).Width + Ui.S(30, s);
            if (control is Label) { horizontal = 0; vertical = Ui.S(4, s); width = TextRenderer.MeasureText(control.Text, control.Font).Width + Ui.S(4, s); }
            width = Math.Max(1, Math.Min(availableW, width));
            Size text = Ui.Measure(control.Text, control.Font, Math.Max(1, width - horizontal));
            int height = Math.Max(Ui.TextH(control.Font, s), text.Height + vertical);
            if (control is ComboBox || control is NumericUpDown) height = Math.Max(height, control.PreferredSize.Height);
            return new Size(width, height);
        }

        private static void Place(Control control, int x, int y, int w, int h, Font font)
        {
            if (!Object.ReferenceEquals(control.Font, font)) control.Font = font;
            control.SetBounds(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        private static int Wrapped(Label label, int x, int y, int width, Font font, double s)
        {
            int height = String.IsNullOrEmpty(label.Text) ? 0 : Ui.Measure(label.Text, font, width).Height + Ui.S(6, s);
            Place(label, x, y, width, Math.Max(1, height), font);
            return y + height;
        }

        private static int FlowControls(Control[] controls, int x, int y, int width, double s, Font font, bool equal)
        {
            int gap = Ui.S(8, s), cx = x, rowH = 0;
            int widest = 0;
            foreach (Control control in controls)
            {
                control.Font = font;
                widest = Math.Max(widest, PreferredControl(control, width, s).Width);
            }
            int columns = equal ? Math.Max(1, Math.Min(controls.Length, (width + gap) / Math.Max(1, widest + gap))) : 0;
            int colW = equal ? Math.Max(1, (width - gap * (columns - 1)) / columns) : 0;
            foreach (Control control in controls)
            {
                Size size = PreferredControl(control, width, s);
                if (equal) size.Width = colW;
                if (cx > x && cx + size.Width > x + width)
                {
                    y += rowH + gap;
                    cx = x;
                    rowH = 0;
                }
                Place(control, cx, y, size.Width, size.Height, font);
                rowH = Math.Max(rowH, control.Height);
                cx += size.Width + gap;
            }
            return y + rowH + gap;
        }

        private static int Title(SectionTitle title, int y, int width, Font font, double s)
        {
            int height = Ui.TextH(font, s) + Ui.S(8, s);
            Place(title, 0, y, width, height, font);
            return y + height + Ui.S(8, s);
        }

        private int LayoutControls(int width, double s, Font body, Font small, Font bold)
        {
            int y = 0, gap = Ui.S(8, s);
            y = Title(_secMode, y, width, bold, s);
            y = FlowControls(_btnMode, 0, y, width, s, body, true);
            y = Wrapped(_lblModeNote, 0, y, width, small, s) + gap;
            y = Title(_secSw, y, width, bold, s);
            y = FlowControls(new Control[] { _chkFn, _chkTp }, 0, y, width, s, body, false) + gap;
            y = Title(_secKbd, y, width, bold, s);
            y = FlowControls(_btnKbd, 0, y, width, s, body, true);
            y = Wrapped(_lblKbdNote, 0, y, width, small, s) + gap;
            y = Title(_secFan, y, width, bold, s);
            y = FlowControls(new Control[] { _btnBoost }, 0, y, width, s, body, false);
            y = Wrapped(_lblBoostNote, 0, y, width, small, s) + gap;
            y = Title(_secOsd, y, width, bold, s);
            y = FlowControls(new Control[] { _lblHintMode, _cmbOsdHint }, 0, y, width, s, body, false);
            y = FlowControls(new Control[] { _btnOsdRestart, _btnOsdDiag, _btnOsdDir, _btnRecap }, 0, y, width, s, body, true);
            y = FlowControls(new Control[] { _chkStartup }, 0, y, width, s, body, false) + gap;
            int buttonH = PreferredControl(_btnPrefs, width, s).Height;
            Place(_btnPrefs, 0, y, width, buttonH, body);
            y += buttonH + gap;
            return y + gap;
        }

        private int LayoutPreferences(double s, int width)
        {
            Font body = Ui.F(9F, false, false, s), small = Ui.F(8F, false, false, s);
            int gap = Ui.S(8, s), y = 0;
            y = Wrapped(_lblTrayTitle, 0, y, width, body, s) + gap;
            _lstTray.Font = body;
            int listW = Ui.S(180, s);
            foreach (object item in _lstTray.Items) listW = Math.Max(listW, TextRenderer.MeasureText(item.ToString(), body).Width + Ui.S(40, s));
            listW = Math.Min(listW, width);
            int listH = _lstTray.Items.Count * _lstTray.ItemHeight + Ui.S(6, s);
            bool split = width >= listW + Ui.S(170, s) + gap;
            Place(_lstTray, 0, y, split ? listW : width, listH, body);
            int previewX = split ? listW + gap : 0;
            int previewW = width - previewX;
            int previewY = split ? y : y + listH + gap;
            int previewH = Math.Max(Ui.S(78, s), Ui.Measure(_lblTrayPreview.Text, small, Math.Max(1, previewW - 2)).Height + Ui.S(12, s));
            Place(_lblTrayPreview, previewX, previewY, previewW, previewH, small);
            int previewEnd = previewY + previewH + gap;
            previewEnd = FlowControls(new Control[] { _lblTrayBudget, _btnTrayDefault }, previewX, previewEnd, previewW, s, body, false);
            y = Math.Max(y + listH + gap, previewEnd) + gap;
            y = FlowControls(new Control[] { _lblTrayIcon, _cmbTrayIcon }, 0, y, width, s, body, false);
            // 阈值单独占行；标签与三个数值框允许按可用宽度换行。
            y = FlowControls(new Control[] { _lblTrayTh, _numTrayTh[0], _numTrayTh[1], _numTrayTh[2] }, 0, y, width, s, body, false);
            y = Wrapped(_lblSwTitle, 0, y, width, body, s) + gap;
            int swatchW = Math.Min(Ui.S(56, s), Math.Max(1, (width - gap * 3) / 4));
            int swatchH = Ui.TextH(body, s) + Ui.S(8, s);
            for (int i = 0; i < _swLevel.Length; i++) Place(_swLevel[i], i * (swatchW + gap), y, swatchW, swatchH, body);
            y += swatchH + gap;
            y = Wrapped(_lblTrayHint, 0, y, width, small, s) + gap;
            y = FlowControls(new Control[] { _btnColorDefault, _btnSensorProbe }, 0, y, width, s, body, false);
            y = FlowControls(new Control[] { _chkColorRaw }, 0, y, width, s, body, false);
            return y;
        }

        private int LayoutSummary(int width, double s, Font body, Font small)
        {
            int pad = Ui.S(16, s), gap = Ui.S(8, s);
            int innerW = Math.Max(1, width - pad * 2);
            Font big = Ui.F(22F, true, true, s);
            int columns = innerW >= Ui.S(450, s) ? 3 : 1;
            int colW = Math.Max(1, (innerW - gap * (columns - 1)) / columns);
            int y = pad, heroH = 0;
            for (int i = 0; i < _big.Length; i++)
            {
                int height = _big[i].Layout(pad + (i % columns) * (colW + gap), y, colW, s, big, body, small, body);
                heroH = Math.Max(heroH, height);
                if (columns == 1) { y += height + gap; heroH = 0; }
            }
            return y + heroH + pad;
        }

        private int LayoutReadout(int width, double s, Font body, Font small)
        {
            int pad = Ui.S(16, s), gap = Ui.S(8, s), y = pad;
            int innerW = Math.Max(1, width - pad * 2);
            Font value = Ui.F(10F, true, true, s);
            int[] starts = new int[] { 0, 4, 9 }, counts = new int[] { 4, 5, 9 };
            for (int cluster = 0; cluster < starts.Length; cluster++)
            {
                if (cluster > 0) { _clusterLines[cluster - 1].SetBounds(pad, y, innerW, 1); y += gap * 2; }
                y = Wrapped(_clusterLabels[cluster], pad, y, innerW, small, s) + gap;
                for (int row = starts[cluster]; row < starts[cluster] + counts[cluster]; row++)
                {
                    if (!_rows[row].Visible) continue;
                    y += _rows[row].Layout(y, innerW, Ui.S(28, s), s, small, value, value, small);
                }
                y += gap;
            }
            y = Wrapped(_lblEnv, pad, y, innerW, small, s);
            y = Wrapped(_lblUnavail, pad, y, innerW, small, s);
            return y + pad;
        }
    }
}
