using System.Drawing.Imaging;

namespace SmbManager.Local;

internal static class UiDiagnostics
{
    public static void Run(Form form, string[] args)
    {
        var file = Wire.Arg(args, "--snapshot", "");
        if (file.Length > 0)
        {
            if (args.Contains("--minimum-size")) form.Size = form.MinimumSize;
            var renderScale=double.Parse(Wire.Arg(args,"--render-scale","1"),System.Globalization.CultureInfo.InvariantCulture);
            if(renderScale is <1 or >2)throw new ArgumentOutOfRangeException("--render-scale");
            if(renderScale!=1) {
                UiTheme.PreviewScale=renderScale;form.AutoScaleMode=AutoScaleMode.None;
                var controls=Descendants(form).Prepend(form).Select(c=>(Control:c,Font:c.Font)).ToArray();
                form.SuspendLayout();form.Scale(new SizeF((float)renderScale,(float)renderScale));
                foreach(var c in controls)c.Control.Font=new(c.Font.FontFamily,c.Font.Size*(float)renderScale,c.Font.Style);
                form.ResumeLayout(true);
            }
            var delay=int.Parse(Wire.Arg(args,"--snapshot-delay-ms","2500"));if(delay<2500||delay>30000)throw new ArgumentOutOfRangeException("--snapshot-delay-ms");
            var timer = new System.Windows.Forms.Timer { Interval = delay };
            form.Shown += (_, _) => timer.Start();
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(file, ImageFormat.Png);
                    var problems = new List<string>();
                    Inspect(form, problems);
                    var scroll = new List<object>();
                    foreach(var viewport in Descendants(form).OfType<Panel>().Where(p=>p.Parent==form && p.AutoScroll && p.VerticalScroll.Visible)) {
                        viewport.AutoScrollPosition=new Point(0,Math.Max(0,viewport.DisplayRectangle.Height-viewport.ClientSize.Height));
                        scroll.Add(new {viewport.ClientSize,viewport.DisplayRectangle,viewport.AutoScrollPosition});
                        using var bottom=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bottom,new Rectangle(Point.Empty,form.Size));bottom.Save(file+".bottom.png",ImageFormat.Png);
                        viewport.AutoScrollPosition=Point.Empty;
                    }
                    Wire.Save(file + ".layout.json", new { window = form.Text, width = form.Width, height = form.Height,
                        dpi = form.DeviceDpi,renderScale,font=form.Font.FontFamily.Name, scroll, problems,
                        layout=Descendants(form).Select(c=>new{type=c.GetType().Name,bounds=c.Bounds,preferred=c.PreferredSize,minimum=c.MinimumSize,autoSize=c.AutoSize,rows=c is TableLayoutPanel t?t.GetRowHeights():null}),
                        note = "renderScale enlarges fonts/geometry for stress QA; it is not a real OS DPI change or antivirus certification." });
                    if (problems.Count > 0) Environment.ExitCode = 1;
                }
                catch (Exception e)
                {
                    File.WriteAllText(file + ".error.txt", e.ToString()); Environment.ExitCode = 1;
                }
                finally { timer.Dispose(); form.Close(); }
            };
        }
        Application.Run(form);
    }
    private static void Inspect(Control parent, List<string> problems)
    {
        if(parent is TableLayoutPanel) {
            var visible=parent.Controls.Cast<Control>().Where(c=>c.Visible).ToArray();
            if(parent is not ScrollableControl {AutoScroll:true})foreach(var child in visible)
                if(child.Right>parent.ClientSize.Width+2 || child.Bottom>parent.ClientSize.Height+2)problems.Add("Table child outside bounds: "+child.GetType().Name);
            for(var i=0;i<visible.Length;i++)for(var j=i+1;j<visible.Length;j++){
                var overlap=Rectangle.Intersect(visible[i].Bounds,visible[j].Bounds);
                if(overlap.Width>2 && overlap.Height>2)problems.Add("Overlapping table controls: "+visible[i].GetType().Name+" / "+visible[j].GetType().Name);
            }
        }
        foreach (Control control in parent.Controls)
        {
            if (!control.Visible) continue;
            if (control is Button b && b.Height < TextRenderer.MeasureText(b.Text, b.Font).Height + 8)
                problems.Add("Button vertical space: " + b.Text);
            if (control is Button or Label && parent is not ScrollableControl { AutoScroll: true }
                && (control.Bottom > parent.ClientSize.Height + 1 || control.Right > parent.ClientSize.Width + 1))
                problems.Add("Control outside parent: " + control.GetType().Name);
            Inspect(control, problems);
        }
    }
    private static IEnumerable<Control> Descendants(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
}
