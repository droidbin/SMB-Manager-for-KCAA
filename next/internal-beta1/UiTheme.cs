using System.Drawing.Text;

namespace SmbManager.Local;

// Shared visual language from the released 1.7.11 UI. No networking or settings changes.
internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(243,243,243);
    public static readonly Color Ink = Color.FromArgb(32,32,32);
    public static readonly Color Muted = Color.FromArgb(96,96,96);
    public static readonly Color Blue = Color.FromArgb(0,103,192);
    public static readonly Color Border = Color.FromArgb(204,204,204);
    private static readonly PrivateFontCollection Fonts = new();
    private static readonly FontFamily Family = LoadFamily();
    public static double PreviewScale {get;set;}=1;
    private static FontFamily LoadFamily()
    {
        foreach(var name in new[]{"NotoSansCJKkr-Regular.otf","NotoSansCJKkr-Bold.otf"}) {
            var file=Path.Combine(AppContext.BaseDirectory,"Fonts",name);
            if(File.Exists(file)) { try { Fonts.AddFontFile(file); } catch(ArgumentException) { } }
        }
        return Fonts.Families.FirstOrDefault() ?? new FontFamily("맑은 고딕");
    }
    public static Font Font(float size=9, bool bold=false) => new(Family,size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Point);
    public static void Form(Form form, Size size, Size minimum)
    {
        form.AutoScaleDimensions=new(96,96);form.AutoScaleMode=AutoScaleMode.Dpi;
        form.Size=size;form.MinimumSize=minimum;form.Font=Font();form.BackColor=Background;form.ForeColor=Ink;
        form.StartPosition=FormStartPosition.CenterScreen;
        form.Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        form.Shown+=(_,_)=>{Compact(form);FitScreen(form);};
        form.DpiChanged+=(_,_)=>form.BeginInvoke(()=>FitScreen(form));
    }
    private static void FitScreen(Form form)
    {
        var area=Screen.FromControl(form).WorkingArea;
        var max=new Size(Math.Max(320,area.Width-16),Math.Max(240,area.Height-16));
        form.MinimumSize=new(Math.Min(form.MinimumSize.Width,max.Width),Math.Min(form.MinimumSize.Height,max.Height));
        form.Size=new(Math.Min(form.Width,max.Width),Math.Min(form.Height,max.Height));
        if(!area.Contains(form.Bounds))form.Location=new(area.Left+Math.Max(0,(area.Width-form.Width)/2),area.Top+Math.Max(0,(area.Height-form.Height)/2));
    }
    private static void Compact(Control root)
    {
        foreach(Control child in root.Controls) {
            if(child is FlowLayoutPanel {AutoSize:true} flow)flow.AutoSizeMode=AutoSizeMode.GrowAndShrink;
            if(child is TableLayoutPanel {AutoSize:true} table)table.AutoSizeMode=AutoSizeMode.GrowAndShrink;
            if(child is TableLayoutPanel {ColumnCount:1,ColumnStyles.Count:0} single)single.ColumnStyles.Add(new(SizeType.Percent,100));
            Compact(child);
        }
    }
    public static TableLayoutPanel Header(string role, Label state, Action close)
    {
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,MinimumSize=new(0,66),ColumnCount=3,BackColor=Color.White,Padding=new(14,6,10,6),Margin=new(0,0,0,10)};
        header.ColumnStyles.Add(new(SizeType.Percent,100));header.ColumnStyles.Add(new(SizeType.AutoSize));header.ColumnStyles.Add(new(SizeType.AutoSize));
        var title=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=Padding.Empty};
        title.Controls.Add(new Label{Text="SMB Manager",AutoSize=true,Font=Font(16,true),Margin=new(0,5,12,0)});
        title.Controls.Add(new Label{Text=role+" · 2.0.0-beta.1 · 내부 시험",AutoSize=true,ForeColor=Muted,Margin=new(0,15,0,0)});
        header.Controls.Add(title,0,0);
        state.AutoSize=true;state.ForeColor=Muted;state.Anchor=AnchorStyles.Right;state.Margin=new(8,6,12,6);header.Controls.Add(state,1,0);
        var exit=new Button{Text="창 닫기",AutoSize=true,Margin=new(0,6,0,6)};Button(exit);exit.Click+=(_,_)=>close();header.Controls.Add(exit,2,0);return header;
    }
    public static void Button(Button button,bool primary=false)
    {
        button.AutoSize=true;button.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        button.MinimumSize=new(Math.Max(82,button.MinimumSize.Width),Math.Max(36,button.MinimumSize.Height));
        button.Padding=new(10,4,10,4);button.Margin=new(3,4,6,4);button.FlatStyle=FlatStyle.Flat;
        button.UseVisualStyleBackColor=false;button.BackColor=primary?Blue:Color.FromArgb(249,249,249);button.ForeColor=primary?Color.White:Ink;
        button.FlatAppearance.BorderColor=primary?Blue:Border;
        button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(0,91,161):Background;
        button.FlatAppearance.MouseDownBackColor=primary?Color.FromArgb(0,79,140):Color.FromArgb(230,230,230);
        button.Font=Font(9,primary);
        void EnabledColors(){button.BackColor=button.Enabled?(primary?Blue:Color.FromArgb(249,249,249)):Color.FromArgb(235,235,235);button.ForeColor=button.Enabled?(primary?Color.White:Ink):Color.FromArgb(110,110,110);button.FlatAppearance.BorderColor=button.Enabled&&primary?Blue:Border;}
        button.EnabledChanged+=(_,_)=>EnabledColors();EnabledColors();
    }
    public static GroupBox Card(string title,Control content)
    {
        var card=new GroupBox{Text=title,Dock=DockStyle.Fill,Padding=new(10),BackColor=Color.White,Margin=new(0,0,10,10)};
        content.Dock=DockStyle.Fill;card.Controls.Add(content);return card;
    }
    public static GroupBox AutoCard(string title,TableLayoutPanel content)
    {
        var card=Card(title,content);card.AutoSize=true;card.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        content.Dock=DockStyle.Top;content.AutoSize=true;content.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        return card;
    }
    public static TableLayoutPanel ScrollingRoot(Form form,int rows,int minimumHeight)
    {
        var viewport=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=Background};
        var root=new TableLayoutPanel{Anchor=AnchorStyles.Top|AnchorStyles.Left,Padding=new(16),ColumnCount=1,RowCount=rows,AutoScroll=false};
        root.ColumnStyles.Add(new(SizeType.Percent,100));
        viewport.Controls.Add(root);form.Controls.Add(viewport);
        bool resizing=false;
        void Resize(){if(resizing)return;resizing=true;try{root.Width=viewport.ClientSize.Width;root.Height=Math.Max(Math.Max(viewport.ClientSize.Height,(int)Math.Ceiling(minimumHeight*form.DeviceDpi/96.0*PreviewScale)),root.GetPreferredSize(new(viewport.ClientSize.Width,0)).Height);}finally{resizing=false;}}
        root.Layout+=(_,_)=>Resize();viewport.SizeChanged+=(_,_)=>Resize();form.Shown+=(_,_)=>Resize();form.DpiChanged+=(_,_)=>Resize();Resize();return root;
    }
    public static void Log(TextBox text)
    {
        text.Multiline=true;text.ReadOnly=true;text.ScrollBars=ScrollBars.Vertical;text.Dock=DockStyle.Fill;
        text.BackColor=Color.FromArgb(17,24,39);text.ForeColor=Color.FromArgb(209,213,219);text.BorderStyle=BorderStyle.FixedSingle;text.Font=Font();
    }
    public static void Grid(DataGridView grid)
    {
        grid.BackgroundColor=Color.White;grid.BorderStyle=BorderStyle.FixedSingle;grid.GridColor=Color.FromArgb(224,224,224);
        grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(249,249,249);
        grid.ColumnHeadersDefaultCellStyle.ForeColor=Ink;grid.ColumnHeadersDefaultCellStyle.Font=Font(9,true);
        grid.DefaultCellStyle.Font=Font();grid.DefaultCellStyle.SelectionBackColor=Color.FromArgb(225,239,250);grid.DefaultCellStyle.SelectionForeColor=Ink;
        grid.AlternatingRowsDefaultCellStyle.BackColor=Color.FromArgb(250,250,250);
    }
    public static void Descendants(Control parent)
    {
        foreach(Control child in parent.Controls) {
            if(child is Button b)Button(b,b.DialogResult==DialogResult.OK || b.Text.StartsWith("배정 저장") || b.Text=="저장");
            if(child is DataGridView grid)Grid(grid);
            if(child is TextBox t && t.ReadOnly){t.BackColor=Color.White;t.ForeColor=Ink;}
            if(child is Label label)label.ForeColor=Ink;
            Descendants(child);
        }
    }
    public static string Error(Exception error)=>error switch {
        OperationCanceledException=>"회사 서버 응답을 기다리다 시간이 초과됐습니다. 연결을 다시 확인하세요.",
        System.Net.Http.HttpRequestException e when e.StatusCode is not null=>e.Message,
        System.Net.Http.HttpRequestException=>"회사 서버에 연결하지 못했습니다. 사내망 연결을 확인하고 다시 시도하세요. 계속되면 서버관리자에게 문의하세요.",
        InvalidOperationException=>error.Message,
        _=>"요청을 완료하지 못했습니다. 다시 시도하거나 서버관리자에게 문의하세요. ("+error.GetType().Name+")"
    };
    public static Label Hint(string text) => new(){Text=text,AutoSize=true,ForeColor=Muted,Margin=new(3,6,3,8)};
    public static void Wrap(Label label,Control container,int padding=24)
    {
        void Update()=>label.MaximumSize=new(Math.Max(120,container.ClientSize.Width-padding),0);
        container.SizeChanged+=(_,_)=>Update();Update();
    }
}
