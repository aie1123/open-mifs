using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using OpenMIFS;

internal static class LayoutHarness
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly List<string> Failures = new List<string>();
    private static readonly List<string> Results = new List<string>();
    private static double CurrentScale = 1;
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RectangleRect value, int size);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RectangleRect { public int Left, Top, Right, Bottom; }

    private static object Field(object obj, string name) { return obj.GetType().GetField(name, Members).GetValue(obj); }
    private static void Call(object obj, string name, params object[] args) { obj.GetType().GetMethod(name, Members).Invoke(obj, args); }
    private static void Assert(bool value, string message) { if (!value) Failures.Add(message); }

    private static void StopTimers(MainForm form)
    {
        foreach (FieldInfo field in typeof(MainForm).GetFields(Members))
            if (field.FieldType == typeof(System.Windows.Forms.Timer)) ((System.Windows.Forms.Timer)field.GetValue(form)).Stop();
    }

    private static void SeedText(MainForm form)
    {
        ((Label)Field(form,"_lblBadgeMode")).Text = "均衡";
        ((Label)Field(form,"_lblBadgeAc")).Text = "Type-C 供电";
        ((Label)Field(form,"_lblBadgeOsd")).Text = "OSD 正常";
        ((Label)Field(form,"_lblBoostNote")).Text = "本机未验证风扇调速接口，保持固件自动控制；此处仅验证界面长说明能完整显示。";
        ((Label)Field(form,"_lblModeNote")).Text = "模式回读显示在此，调整窗口时说明应换行。";
        ((Label)Field(form,"_lblKbdNote")).Text = "键盘背光由固件提供";
        ((Label)Field(form,"_lblTrayPreview")).Text = "布局测试文本（非硬件读数）\r\nCPU / GPU / 风扇 / 内存 / 电池";
        ((Label)Field(form,"_lblTrayHint")).Text = "低于阈值为第一档，超过三项阈值依次进入第二、第三和第四档；这里验证长说明换行。";
        ((Label)Field(form,"_lblSensorHint")).Text = "布局测试：以下数字均为排版样本，不是传感器实测。";
        ((Label)Field(form,"_lblUnavail")).Text = "不可用项保留原因说明，长内容允许换行，不遮住最后一行电池。";
        foreach (BigReadout readout in (BigReadout[])Field(form,"_big")) readout.Set("123.45","RPM","排版样本",0,true);
        IList rows = (IList)Field(form,"_rows");
        for (int i=0;i<rows.Count;i++) { ReadoutRow row=(ReadoutRow)rows[i]; row.Visible=true; row.Set(i==9?"11.9 / 31.2":"1234.5",i==9?"GB":"MHz",true,0); }
    }

    private static void CheckChildren(Control parent, string tag)
    {
        List<Control> visible = new List<Control>();
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            visible.Add(child);
            if (!(parent is ScrollableControl) || !((ScrollableControl)parent).AutoScroll)
                Assert(child.Left>=-1 && child.Top>=-1 && child.Right<=parent.ClientSize.Width+2 && child.Bottom<=parent.ClientSize.Height+2,
                    tag+" bounds: "+child.GetType().Name+" "+child.Text+" "+child.Bounds+" parent="+parent.GetType().Name+" "+parent.ClientSize);
            Label label=child as Label;
            if(label!=null && label.Text.Length>0)
            {
                Size need=TextRenderer.MeasureText(label.Text,label.Font,new Size(Math.Max(1,label.ClientSize.Width),int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
                Assert(need.Height<=label.ClientSize.Height+3,tag+" text: "+label.Text+" need="+need+" actual="+label.ClientSize);
            }
            if((child is FlatButton || child is SectionTitle || child is CheckBox) && child.Text.Length>0)
            {
                int inset=child is CheckBox?(int)Math.Round(28*CurrentScale):(child is FlatButton?12:0);
                Size need=Ui.Measure(child.Text,child.Font,Math.Max(1,child.Width-inset));
                Assert(need.Height<=child.Height,tag+" action-text: "+child.Text+" need="+need+" actual="+child.Size);
            }
            CheckChildren(child,tag);
        }
        for(int i=0;i<visible.Count;i++)for(int j=i+1;j<visible.Count;j++)
        {
            Rectangle intersection=Rectangle.Intersect(visible[i].Bounds,visible[j].Bounds);
            Assert(intersection.Width<=1 || intersection.Height<=1,tag+" overlap: "+visible[i].Text+" / "+visible[j].Text+" at "+intersection);
        }
    }

    private static void CheckScroll(MainForm form,string tag)
    {
        FieldInfo field=typeof(MainForm).GetField("_viewport",Members);
        if(field==null)return;
        Panel viewport=(Panel)field.GetValue(form);
        Assert(!viewport.HorizontalScroll.Visible,tag+" horizontal scroll appeared");
        Panel prefs=(Panel)Field(form,"_pnlPrefs");
        if(prefs.Visible)
        {
            Control target=(Control)Field(form,"_chkColorRaw");
            viewport.ScrollControlIntoView(target);
            Application.DoEvents();
            Rectangle targetRect=viewport.RectangleToClient(target.Parent.RectangleToScreen(target.Bounds));
            Assert(targetRect.Top>=0 && targetRect.Bottom<=viewport.ClientSize.Height,tag+" final preference cannot be scrolled into view: "+targetRect+" viewport="+viewport.ClientRectangle);
        }
        viewport.AutoScrollPosition=Point.Empty;
        Application.DoEvents();
    }

    private static void SetClientSizeForTest(MainForm form,Size size)
    {
        // Form.ClientSize passes through Form.SetBoundsCore, which clamps large tests to the runner desktop.
        // Set the native window bounds instead, then require the requested client size before measuring layout.
        int frameWidth=form.Width-form.ClientSize.Width;
        int frameHeight=form.Height-form.ClientSize.Height;
        if(!SetWindowPos(form.Handle,IntPtr.Zero,0,0,size.Width+frameWidth,size.Height+frameHeight,0x0016))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Application.DoEvents();
        form.PerformLayout();
        if(form.ClientSize!=size)throw new Exception("Requested layout size "+size+" but actual client size is "+form.ClientSize);
    }

    private static void Screenshot(MainForm form,string path)
    {
        SetWindowPos(form.Handle,new IntPtr(-1),40,40,0,0,0x0001|0x0040);
        Application.DoEvents();
        RectangleRect rect;
        if(DwmGetWindowAttribute(form.Handle,9,out rect,Marshal.SizeOf(typeof(RectangleRect)))!=0) throw new Exception("DWM capture bounds failed");
        using(Bitmap bmp=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top))
        { using(Graphics g=Graphics.FromImage(bmp))g.CopyFromScreen(rect.Left,rect.Top,0,0,bmp.Size); bmp.Save(path,ImageFormat.Png); }
    }

    [STAThread] private static int Main(string[] args)
    {
        try
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output=Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            bool baseline=Array.IndexOf(args,"--baseline")>=0;
            bool screenshots=Array.IndexOf(args,"--no-screenshots")<0;
            using(MainForm form=new MainForm())
            {
                StopTimers(form);
                form.StartPosition=FormStartPosition.Manual;
                form.Location=new Point(40,40);
                form.Show();
                form.MaximumSize=new Size(16384,16384); // Allow native test windows beyond the runner desktop.
                StopTimers(form);
                SeedText(form);
                typeof(MainForm).GetField("_prefsOpen",Members).SetValue(form,true);
                Size[] sizes=baseline?new Size[]{new Size(720,600)}:new Size[]{new Size(480,360),new Size(560,420),new Size(720,600),new Size(940,720),new Size(1200,900),new Size(1500,900)};
                double[] scales=baseline?new double[]{1}:new double[]{1,1.25,1.5,1.75,2,2.5};
                foreach(double scale in scales)
                {
                    CurrentScale=scale;
                    FieldInfo scaleOverride=typeof(MainForm).GetField("_testDpiScale",Members);
                    if(scaleOverride!=null)scaleOverride.SetValue(form,scale);
                    foreach(Size size in sizes)
                    {
                        foreach(bool prefsOpen in baseline?new bool[]{true}:new bool[]{false,true})
                        foreach(bool elevate in baseline?new bool[]{false}:new bool[]{false,true})
                        {
                        typeof(MainForm).GetField("_prefsOpen",Members).SetValue(form,prefsOpen);
                        ((Control)Field(form,"_btnElevate")).Visible=elevate;
                        SetClientSizeForTest(form,size);
                        Control viewport=typeof(MainForm).GetField("_viewport",Members)==null?null:(Control)Field(form,"_viewport");
                        if(viewport!=null)((Panel)viewport).AutoScrollPosition=Point.Empty;
                        Call(form,"LayoutAll",true);
                        Application.DoEvents();
                        int before=Failures.Count;
                        string tag="scale="+scale+" size="+size+" prefs="+prefsOpen+" elevate="+elevate;
                        CheckChildren(form,tag);
                        CheckScroll(form,tag);
                        Results.Add(tag+" failures="+(Failures.Count-before));
                        if(screenshots && scale==1 && prefsOpen && !elevate && (size.Width==720 || size.Width==1200))
                        {
                            Screenshot(form,Path.Combine(output,"layout-"+size.Width+"x"+size.Height+".png"));
                            if(viewport!=null)
                            {
                                ((Panel)viewport).ScrollControlIntoView((Control)Field(form,"_chkColorRaw"));
                                Screenshot(form,Path.Combine(output,"layout-"+size.Width+"x"+size.Height+"-bottom.png"));
                            }
                        }
                        }
                    }
                }
                if(!baseline)
                {
                    typeof(MainForm).GetField("_testDpiScale",Members).SetValue(form,1.0);
                    CurrentScale=1;
                    SetClientSizeForTest(form,new Size(SystemInformation.MaxWindowTrackSize.Width+64,SystemInformation.MaxWindowTrackSize.Height+64));
                    Call(form,"LayoutAll",true);
                    CheckChildren(form,"beyond-desktop-size");
                    form.MaximumSize=Size.Empty;
                    form.WindowState=FormWindowState.Maximized;
                    Application.DoEvents();
                    Call(form,"LayoutAll",true);
                    CheckChildren(form,"maximized");
                    form.WindowState=FormWindowState.Normal;
                    Application.DoEvents();
                    form.MaximumSize=new Size(16384,16384);
                    SetClientSizeForTest(form,new Size(SystemInformation.MaxWindowTrackSize.Width+64,SystemInformation.MaxWindowTrackSize.Height+64));
                    Call(form,"LayoutAll",true);
                    CheckChildren(form,"restored-beyond-desktop-size");
                    for(int i=0;i<20;i++)
                    {
                        SetClientSizeForTest(form,new Size(i%2==0?560:1200,i%2==0?420:800));
                        Call(form,"LayoutAll",true);
                        Application.DoEvents();
                    }
                    form.HideToTray();
                    Assert(!form.Visible,"hide-to-tray failed");
                    form.Show();
                    form.WindowState=FormWindowState.Normal;
                    Call(form,"LayoutAll",true);
                    CheckChildren(form,"restore-after-resize");
                }
                form.ForceClose();
            }
            File.WriteAllLines(Path.Combine(output,"results.txt"),Results.ToArray(),Encoding.UTF8);
            File.WriteAllLines(Path.Combine(output,"failures.txt"),Failures.ToArray(),Encoding.UTF8);
            Console.WriteLine("LAYOUT_TEST_"+(Failures.Count==0?"OK":"FAIL")+" scenarios="+Results.Count+" failures="+Failures.Count);
            foreach(string failure in Failures.GetRange(0,Math.Min(8,Failures.Count))) Console.WriteLine(failure);
            return Failures.Count==0?0:1;
        }
        catch(Exception ex){Console.WriteLine(ex.ToString());return 2;}
    }
}
