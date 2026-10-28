using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p,IntPtr after,string cls,string title);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumProc(IntPtr h,IntPtr state);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h,EnumProc callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder name,int max);
    public static IntPtr FilePane(IntPtr root) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(root,delegate(IntPtr h,IntPtr state) {
            var name=new StringBuilder(256); GetClassName(h,name,256);
            if(name.ToString()=="SHELLDLL_DefView" && IsWindowVisible(h)) { found=h; return false; }
            return true;
        },IntPtr.Zero);
        return found;
    }
    public static string ProcessName(IntPtr h) {
        uint pid; GetWindowThreadProcessId(h,out pid);
        try { using(var p=Process.GetProcessById((int)pid)) return p.ProcessName; } catch { return ""; }
    }
}

static class PhotoWriter {
    static string TextFrom(IDataObject data) {
        foreach(var format in new[]{DataFormats.UnicodeText,DataFormats.StringFormat,DataFormats.Text}) {
            if(!data.GetDataPresent(format)) continue;
            var text=data.GetData(format) as string;
            if(!String.IsNullOrEmpty(text)) return text;
        }
        return null;
    }
    public static bool Accepts(IDataObject data) {
        return data!=null && (data.GetDataPresent("image/jpeg") || data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.FileDrop) || TextFrom(data)!=null);
    }
    static byte[] Bytes(object value) {
        var bytes=value as byte[]; if(bytes!=null) return bytes;
        var stream=value as Stream; if(stream==null) return null;
        long pos=stream.CanSeek ? stream.Position : 0;
        try { if(stream.CanSeek) stream.Position=0; using(var result=new MemoryStream()) { stream.CopyTo(result); return result.ToArray(); } }
        finally { if(stream.CanSeek) stream.Position=pos; }
    }
    static string CopyFile(string source,string folder) {
        string name=Path.GetFileName(source);
        if(String.IsNullOrEmpty(name) || Directory.Exists(source)) throw new IOException("Перетаскивай файл, а не папку.");
        string stem=Path.GetFileNameWithoutExtension(name),ext=Path.GetExtension(name);
        // Stream large attachments instead of loading the entire file into memory.
        using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            for(int number=0;number<10000;number++) {
                string path=Path.Combine(folder,number==0 ? name : stem+" ("+number+")"+ext);
                FileStream output;
                try { output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None); }
                catch(IOException) { if(File.Exists(path) || Directory.Exists(path)) continue; throw; }
                try { using(output) input.CopyTo(output); }
                catch { try { File.Delete(path); } catch {} throw; }
                return path;
            }
        }
        throw new IOException("Не удалось подобрать свободное имя файла.");
    }
    public static string Save(IDataObject data,string folder) {
        if(!Directory.Exists(folder)) throw new IOException("Папка больше не существует.");
        // A real attachment takes priority over its thumbnail, caption or URL.
        if(data.GetDataPresent(DataFormats.FileDrop)) {
            var files=data.GetData(DataFormats.FileDrop) as string[];
            if(files==null || files.Length!=1) throw new IOException("Перетаскивай по одному файлу.");
            return CopyFile(files[0],folder);
        }
        byte[] bytes=null; string ext=".png";
        if(data.GetDataPresent("image/jpeg")) {
            bytes=Bytes(data.GetData("image/jpeg"));
            if(bytes!=null && bytes.Length>3 && bytes[0]==255 && bytes[1]==216) ext=".jpg";
            else bytes=null;
        }
        if(bytes==null && data.GetDataPresent("PNG")) {
            var candidate=Bytes(data.GetData("PNG"));
            if(candidate!=null && candidate.Length>8 && candidate[0]==137 && candidate[1]==80 && candidate[2]==78 && candidate[3]==71) bytes=candidate;
        }
        if(bytes==null && data.GetDataPresent(DataFormats.Bitmap)) {
            var image=data.GetData(DataFormats.Bitmap) as Image;
            if(image!=null) using(var stream=new MemoryStream()) { image.Save(stream,ImageFormat.Png); bytes=stream.ToArray(); }
        }
        if(bytes==null) {
            var text=TextFrom(data);
            if(text!=null) {
                ext=".txt";
                // BOM lets Windows Notepad recognize UTF-8, including Cyrillic and emoji.
                var encoding=new UTF8Encoding(true);
                using(var stream=new MemoryStream()) {
                    var preamble=encoding.GetPreamble(); stream.Write(preamble,0,preamble.Length);
                    var content=encoding.GetBytes(text); stream.Write(content,0,content.Length);
                    bytes=stream.ToArray();
                }
            }
        }
        if(bytes==null || bytes.Length==0) throw new IOException("Telegram не передал файл, фото или текст. Дождись полной загрузки вложения.");
        var name="Telegram_"+DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,8)+ext;
        var path=Path.Combine(folder,name);
        bool created=false;
        try { using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { created=true; file.Write(bytes,0,bytes.Length); } }
        catch { if(created) { try { File.Delete(path); } catch {} } throw; }
        return path;
    }
}

class Receiver : Form {
    readonly Label label=new Label();
    public string Folder;
    public Action<string> Saved;
    public Action<string> Failed;
    public Receiver() {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
        BackColor=Color.FromArgb(30,125,180); Opacity=0.25; AllowDrop=true;
        label.Text="Отпусти файл, фото или текст — сохранить сюда";
        label.ForeColor=Color.White; label.Font=new Font("Segoe UI",15,FontStyle.Bold);
        label.Dock=DockStyle.Top; label.Height=65; label.TextAlign=ContentAlignment.MiddleCenter;
        label.Enabled=false; Controls.Add(label);
        DragEnter+=EnterDrag; DragOver+=EnterDrag;
        DragDrop+=delegate(object s,DragEventArgs e) {
            try { if((e.AllowedEffect & DragDropEffects.Copy)==0) return; var path=PhotoWriter.Save(e.Data,Folder); e.Effect=DragDropEffects.Copy; if(Saved!=null) Saved(path); }
            catch(Exception ex) { e.Effect=DragDropEffects.None; if(Failed!=null) Failed(ex.Message); }
            finally { Hide(); }
        };
    }
    void EnterDrag(object s,DragEventArgs e) {
        try { e.Effect=PhotoWriter.Accepts(e.Data) ? (e.AllowedEffect & DragDropEffects.Copy) : DragDropEffects.None; }
        catch { e.Effect=DragDropEffects.None; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=0x08000000|0x80; return cp; } }
}

class MainForm : Form {
    readonly Timer timer=new Timer();
    readonly Receiver receiver=new Receiver();
    readonly Label status=new Label();
    readonly CheckBox enabled=new CheckBox();
    readonly NotifyIcon tray=new NotifyIcon();
    bool wasDown,tracking;
    Point start;
    DateTime released=DateTime.MinValue;
    readonly bool testSource;
    public MainForm(bool test) {
        testSource=test;
        Text="TgDrop — файлы, фото и текст из Telegram"; ClientSize=new Size(570,310);
        Font=new Font("Segoe UI",10); FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false;
        var text=new Label { Left=22,Top=18,Width=525,Height=140,Text="1. Открой нужную папку в Проводнике.\n2. Расположи Telegram и папку рядом.\n3. Тяни файл, фото или выделенный текст в область файлов.\n4. Появится синяя подсветка — отпускай.\n\nТекст → .txt. Файлы → копия с исходным именем.\nПеред переносом дождись загрузки вложения в Telegram." };
        enabled.SetBounds(22,165,520,30); enabled.Text="Включить перетаскивание"; enabled.Checked=true;
        status.SetBounds(22,205,525,58); status.Text="Готово. Ожидаю перетаскивание из Telegram.";
        var hide=new Button { Left=22,Top=268,Width=195,Height=29,Text="Свернуть в трей" };
        hide.Click+=delegate { Hide(); tray.ShowBalloonTip(2500,"TgDrop","Работает в трее. Двойной щелчок по значку — открыть.",ToolTipIcon.Info); };
        Controls.AddRange(new Control[]{text,enabled,status,hide});
        tray.Icon=SystemIcons.Application; tray.Text="TgDrop: файлы, фото и текст из Telegram"; tray.Visible=true;
        var menu=new ContextMenuStrip(); menu.Items.Add("Открыть",null,delegate { Show(); Activate(); });
        menu.Items.Add("Выход",null,delegate { Close(); }); tray.ContextMenuStrip=menu;
        tray.DoubleClick+=delegate { Show(); Activate(); };
        receiver.Saved=delegate(string path) { status.Text="Сохранено: "+path; tracking=false; tray.ShowBalloonTip(1800,"Файл сохранён",Path.GetFileName(path),ToolTipIcon.Info); };
        receiver.Failed=delegate(string message) { status.Text=message; tracking=false; tray.ShowBalloonTip(3500,"Не удалось сохранить",message,ToolTipIcon.Warning); };
        timer.Interval=35; timer.Tick+=Tick; timer.Start();
        FormClosed+=delegate { timer.Stop(); timer.Dispose(); receiver.Dispose(); tray.Dispose(); };
    }
    internal bool TryFolder(IntPtr root,out string folder,out Rectangle bounds) {
        folder=null; bounds=Rectangle.Empty;
        if(Native.ProcessName(root)!="explorer" || Native.IsIconic(root)) return false;
        // Only the file pane is a destination: never cover the address bar or navigation tree.
        var view=Native.FilePane(root);
        if(view==IntPtr.Zero) return false;
        Native.Rect r; if(!Native.GetWindowRect(view,out r)) return false;
        bounds=Rectangle.FromLTRB(r.L,r.T,r.R,r.B);
        object shell=null,windows=null;
        try {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            windows=((dynamic)shell).Windows();
            int count=((dynamic)windows).Count;
            for(int i=0;i<count;i++) {
                object window=null,document=null,fo=null,self=null;
                try {
                    window=((dynamic)windows).Item(i);
                    if(new IntPtr((long)((dynamic)window).HWND)!=root) continue;
                    document=((dynamic)window).Document; fo=((dynamic)document).Folder; self=((dynamic)fo).Self;
                    string path=((dynamic)self).Path;
                    if(Directory.Exists(path)) { folder=path; return true; }
                } catch {} finally { Release(self); Release(fo); Release(document); Release(window); }
            }
        } catch {} finally { Release(windows); Release(shell); }
        return false;
    }
    static void Release(object o) { if(o!=null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
    void Tick(object sender,EventArgs e) {
        bool down=(Native.GetAsyncKeyState(1)&0x8000)!=0;
        if(!enabled.Checked) { tracking=false; receiver.Hide(); wasDown=down; return; }
        if(down && !wasDown) {
            var source=Native.ProcessName(Native.GetForegroundWindow());
            tracking=source.Equals("Telegram",StringComparison.OrdinalIgnoreCase) || (testSource && source==Process.GetCurrentProcess().ProcessName);
            start=Cursor.Position;
        }
        if(!down && wasDown) released=DateTime.UtcNow;
        wasDown=down;
        if(!down) {
            if((DateTime.UtcNow-released).TotalMilliseconds>350) { tracking=false; receiver.Hide(); }
            return;
        }
        if(!tracking) return;
        var point=Cursor.Position;
        if(Math.Abs(point.X-start.X)+Math.Abs(point.Y-start.Y)<10) return;
        if(receiver.Visible && receiver.Bounds.Contains(point)) return;
        receiver.Hide();
        var root=Native.GetAncestor(Native.WindowFromPoint(point),2);
        string folder; Rectangle bounds;
        if(TryFolder(root,out folder,out bounds) && bounds.Contains(point)) {
            receiver.Folder=folder; receiver.Bounds=bounds; receiver.Show();
        }
    }
}

static class Program {
    [STAThread] static void Main(string[] args) {
        Native.SetProcessDPIAware(); Application.EnableVisualStyles();
        if(args.Length>0 && args[0]=="--self-test") {
            try { Test(); }
            catch(Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test-result.txt"),"FAIL: "+ex); Environment.Exit(1); }
            return;
        }
        if(args.Length>0 && args[0]=="--diagnose") {
            var lines=new List<string>();
            using(var form=new MainForm(false)) {
                foreach(var process in Process.GetProcessesByName("explorer")) using(process) {
                    var h=process.MainWindowHandle;
                    if(h==IntPtr.Zero) continue;
                    string folder; Rectangle bounds;
                    bool found=form.TryFolder(h,out folder,out bounds);
                    lines.Add("Explorer "+h+": pane="+Native.FilePane(h)+"; resolved="+found+"; folder="+folder+"; bounds="+bounds);
                }
            }
            if(lines.Count==0) lines.Add("No Explorer folder windows open.");
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.txt"),lines); return;
        }
        if(args.Length>0 && args[0]=="--drag-test-source") {
            var form=new Form { Text="TgDrop — тестовый источник фото",Width=360,Height=260,StartPosition=FormStartPosition.Manual,Location=new Point(40,150) };
            var label=new Label { Dock=DockStyle.Fill,BackColor=Color.CornflowerBlue,Text="ТЕСТОВОЕ ФОТО\nПеретащи в папку",TextAlign=ContentAlignment.MiddleCenter };
            label.MouseDown+=delegate(object s,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left) return;
                using(var bitmap=new Bitmap(71,53)) using(var stream=new MemoryStream()) {
                    using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.CornflowerBlue);
                    bitmap.Save(stream,ImageFormat.Jpeg);
                    var data=new DataObject(); data.SetData("image/jpeg",new MemoryStream(stream.ToArray()));
                    data.SetData(DataFormats.Bitmap,bitmap);
                    label.DoDragDrop(data,DragDropEffects.Copy);
                }
            };
            form.Controls.Add(label); Application.Run(form); return;
        }
        bool created;
        using(var mutex=new System.Threading.Mutex(true,"Local\\TgDrop-PhotoReceiver",out created)) {
            if(!created) { MessageBox.Show("TgDrop уже работает. Открой его значок в трее."); return; }
            var main=new MainForm(args.Length>0 && args[0]=="--test-source");
            if(Array.IndexOf(args,"--tray")>=0) main.Shown+=delegate { main.BeginInvoke(new Action(delegate { main.Hide(); })); };
            Application.Run(main);
        }
    }
    static void Test() {
        string folder=Path.Combine(Path.GetTempPath(),"TgDrop-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var paths=new List<string>();
        try {
            using(var image=new Bitmap(17,23)) using(var stream=new MemoryStream()) {
                image.SetPixel(0,0,Color.Red); image.Save(stream,ImageFormat.Jpeg);
                var bytes=stream.ToArray(); var data=new DataObject(); data.SetData("image/jpeg",new MemoryStream(bytes));
                if(!PhotoWriter.Accepts(data)) throw new Exception("JPEG detection");
                var first=PhotoWriter.Save(data,folder); paths.Add(first);
                var second=PhotoWriter.Save(data,folder); paths.Add(second);
                if(first==second || Convert.ToBase64String(File.ReadAllBytes(first))!=Convert.ToBase64String(bytes)) throw new Exception("Original JPEG or collision handling");
                var bitmapData=new DataObject(); bitmapData.SetData(DataFormats.Bitmap,image);
                var png=PhotoWriter.Save(bitmapData,folder); paths.Add(png);
                using(var check=Image.FromFile(png)) if(check.Width!=17 || check.Height!=23) throw new Exception("Bitmap conversion");
                var text="Привет, мир!\r\nВторая строка\nEmoji: \U0001F642\n  Пробелы  ";
                var textData=new DataObject(); textData.SetData(DataFormats.UnicodeText,text);
                if(!PhotoWriter.Accepts(textData)) throw new Exception("Unicode text detection");
                var txt=PhotoWriter.Save(textData,folder); paths.Add(txt);
                if(Path.GetExtension(txt)!=".txt" || File.ReadAllText(txt,Encoding.UTF8)!=text) throw new Exception("Unicode text or line breaks changed");
                var textBytes=File.ReadAllBytes(txt);
                if(textBytes[0]!=239 || textBytes[1]!=187 || textBytes[2]!=191) throw new Exception("UTF-8 BOM missing");
                var plain=new DataObject(DataFormats.Text,"plain text");
                var plainPath=PhotoWriter.Save(plain,folder); paths.Add(plainPath);
                if(File.ReadAllText(plainPath)!="plain text") throw new Exception("Plain text fallback");
                var repeat=PhotoWriter.Save(textData,folder); paths.Add(repeat);
                if(repeat==txt || File.ReadAllText(txt)!=text) throw new Exception("Text collision handling");
                bool rejected=false;
                var unsupported=new DataObject(); unsupported.SetData("TgDrop.Unsupported",new byte[]{1,2,3});
                try { PhotoWriter.Save(unsupported,folder); } catch(IOException) { rejected=true; }
                if(!rejected || PhotoWriter.Accepts(unsupported)) throw new Exception("Unsupported data accepted");
                data.SetData(DataFormats.UnicodeText,"Photo caption");
                var mixed=PhotoWriter.Save(data,folder); paths.Add(mixed);
                if(Path.GetExtension(mixed)!=".jpg" || Convert.ToBase64String(File.ReadAllBytes(mixed))!=Convert.ToBase64String(bytes)) throw new Exception("Caption replaced photo");
                var source=Path.Combine(folder,"source.jpg"); File.WriteAllBytes(source,bytes); paths.Add(source);
                var files=new DataObject(); files.SetData(DataFormats.FileDrop,new[]{source});
                var copied=PhotoWriter.Save(files,folder); paths.Add(copied);
                if(Convert.ToBase64String(File.ReadAllBytes(copied))!=Convert.ToBase64String(bytes) || !File.Exists(source)) throw new Exception("File copy changed source");
                if(Path.GetFileName(copied)!="source (1).jpg") throw new Exception("Same-folder collision naming");
                var target=Path.Combine(folder,"destination"); Directory.CreateDirectory(target);
                try {
                    var binary=new byte[2*1024*1024+13]; new Random(42).NextBytes(binary);
                    foreach(var name in new[]{"Документ.pdf","архив.zip","программа.exe","без расширения","пустой.bin"}) {
                        var original=Path.Combine(folder,name);
                        var expected=name=="пустой.bin" ? new byte[0] : binary;
                        File.WriteAllBytes(original,expected); paths.Add(original);
                        var attachment=new DataObject(); attachment.SetData(DataFormats.FileDrop,new[]{original});
                        attachment.SetData(DataFormats.Bitmap,image); attachment.SetData(DataFormats.UnicodeText,"caption");
                        var saved=PhotoWriter.Save(attachment,target); paths.Add(saved);
                        if(Path.GetFileName(saved)!=name || Convert.ToBase64String(File.ReadAllBytes(saved))!=Convert.ToBase64String(expected)) throw new Exception("Attachment name, bytes or priority: "+name);
                        var again=PhotoWriter.Save(attachment,target); paths.Add(again);
                        if(Path.GetFileName(again)!=Path.GetFileNameWithoutExtension(name)+" (1)"+Path.GetExtension(name)) throw new Exception("Attachment collision name");
                        if(Convert.ToBase64String(File.ReadAllBytes(original))!=Convert.ToBase64String(expected) || Convert.ToBase64String(File.ReadAllBytes(saved))!=Convert.ToBase64String(expected)) throw new Exception("Attachment overwrite or source changed");
                    }
                } finally {
                    foreach(var path in paths) if(Path.GetDirectoryName(path)==target) File.Delete(path);
                    Directory.Delete(target);
                }
                var pngBytes=File.ReadAllBytes(png); var pngData=new DataObject(); pngData.SetData("PNG",new MemoryStream(pngBytes));
                var nativePng=PhotoWriter.Save(pngData,folder); paths.Add(nativePng);
                if(Convert.ToBase64String(File.ReadAllBytes(nativePng))!=Convert.ToBase64String(pngBytes)) throw new Exception("Native PNG changed");
            }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test-result.txt"),"PASS: original JPEG bytes; unique names; bitmap to PNG; unsupported data rejected; native PNG bytes preserved; Cyrillic and emoji in UTF-8 with BOM; exact line breaks and spaces; plain text fallback; text collision handling; image takes priority over caption; arbitrary attachments streamed; original filenames and bytes preserved; numbered collisions without overwrite; same-folder copy; PDF, ZIP, EXE, extensionless and empty files; file takes priority over thumbnail and caption; source files unchanged.");
        } finally { foreach(var p in paths) if(File.Exists(p)) File.Delete(p); Directory.Delete(folder); }
    }
}
