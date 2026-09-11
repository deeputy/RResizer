using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RResizer;

public partial class MainWindow : Window
{
    readonly SolidColorBrush normal = new(WpfColor.FromRgb(113,128,150));
    readonly SolidColorBrush error = new(WpfColor.FromRgb(217,75,91));

    private void Author_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "http://ryabov.bz",
                UseShellExecute = true
            });
        }
        catch
        {
            // Не показываем пользователю системный алерт.
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = WindowHandle;
        if (hwnd == 0)
            return;

        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));
    }

    void SizeBoxChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
    {
        Normalize(WidthBox); Normalize(HeightBox);
        Status.Foreground=normal;
        bool w=int.TryParse(WidthBox.Text,out _), h=int.TryParse(HeightBox.Text,out _);
        Status.Text = "или просто нажмите сюда";
    }

    static void Normalize(System.Windows.Controls.TextBox b)
    {
        if(b.Text.Length>1 && b.Text[0]=='0'){int c=b.CaretIndex;b.Text=b.Text.TrimStart('0');b.CaretIndex=Math.Min(c,b.Text.Length);}
    }

    void NumericInput(object s, TextCompositionEventArgs e)
    {
        if(!e.Text.All(char.IsDigit)){e.Handled=true;return;}
        if(s is System.Windows.Controls.TextBox b){
            var p=b.Text.Remove(b.SelectionStart,b.SelectionLength).Insert(b.SelectionStart,e.Text);
            // Ноль не может быть первым символом — даже один "0".
            e.Handled = p.Length > 0 && p[0] == '0';
        }
    }

    void NumericPaste(object s, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if(!e.DataObject.GetDataPresent(DataFormats.Text))return;
        var d=new string(((string?)e.DataObject.GetData(DataFormats.Text)??"").Where(char.IsDigit).ToArray());
        if(s is System.Windows.Controls.TextBox b && d.Length>0){
            var p=b.Text.Remove(b.SelectionStart,b.SelectionLength).Insert(b.SelectionStart,d);
            // При вставке тоже не разрешаем ведущий ноль.
            p = p.TrimStart('0');
            if(p.Length>0){b.Text=p;b.CaretIndex=p.Length;}
        }
    }

    void Preset(object s, RoutedEventArgs e)
    {
        if(s is System.Windows.Controls.Button b){
            var p=(b.Tag?.ToString()??"").Split('|');
            WidthBox.Text=p[0]; HeightBox.Text=p[1];
        }
    }

    void DropClick(object s, MouseButtonEventArgs e)
    {
        // A double-click produces two MouseLeftButtonUp events.
        // The first one opens the file dialog; the second must not open
        // another dialog after the first one closes.
        e.Handled = true;
        if (e.ClickCount != 1)
            return;

        Choose();
    }
    private bool _openingFileDialog;

    void DropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // Handle the click here, before MouseUp can bubble or be interpreted
        // by another element. A guard prevents a second dialog from opening
        // when Explorer returns focus after the first dialog closes.
        e.Handled = true;

        if (_openingFileDialog)
            return;

        _openingFileDialog = true;
        try
        {
            Choose();
        }
        finally
        {
            _openingFileDialog = false;
        }
    }

    void DropDragEnter(object s, DragEventArgs e)
    {
        SetDropEffect(e);
    }

    void DropDragOver(object s, DragEventArgs e)
    {
        SetDropEffect(e);
    }

    void SetDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    async void DropFiles(object s, DragEventArgs e)
    {
        if(e.Data.GetData(DataFormats.FileDrop) is string[] f)
        {
            e.Handled = true;
            await Process(f);
        }
    }

    void Choose()
    {
        if(!int.TryParse(WidthBox.Text,out var w)&&!int.TryParse(HeightBox.Text,out var h)){
            Status.Foreground=error;Status.Text="Сначала укажи ширину или высоту";WidthBox.Focus();return;
        }
        var d=new OpenFileDialog{Multiselect=true,Filter="Изображения|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff"};
        if(d.ShowDialog()==true)_=Process(d.FileNames);
    }

    async Task Process(string[] files)
    {
        int tw=int.TryParse(WidthBox.Text,out var w)?w:0;
        int th=int.TryParse(HeightBox.Text,out var h)?h:0;
        if(tw<=0&&th<=0){Status.Foreground=error;Status.Text="Сначала укажи ширину или высоту";return;}
        // Читаем состояние CheckBox в UI-потоке. Нельзя обращаться к WPF-контролу
        // из Task.Run — это вызывает cross-thread exception и валит обработку.
        bool noUpscale = NoUpscaleCheck.IsChecked == true;
        bool replaceOriginal = ReplaceOriginalCheck.IsChecked == true;

        Status.Foreground=normal;Status.Text=$"Обрабатываем {files.Length} изображений…";
        int done=0;var errors=new List<string>();
        await Task.Run(()=>{
            foreach(var path in files)try{
                using var im=Image.Load(path);int sw=im.Width,sh=im.Height;
                int ow,oh;
                if(tw>0&&th>0){ow=tw;oh=th;}
                else if(tw>0){ow=tw;oh=Math.Max(1,(int)Math.Round(sh*(double)tw/sw));}
                else {oh=th;ow=Math.Max(1,(int)Math.Round(sw*(double)th/sh));}

                // Не увеличиваем изображение, если оно уже меньше заданного размера.
                // Для одного параметра достаточно проверить соответствующую сторону;
                // для двух — только если исходное изображение целиком помещается в W×H.
                bool fitsTarget = tw>0&&th>0
                    ? sw<=tw && sh<=th
                    : tw>0
                        ? sw<=tw
                        : sh<=th;

                if(!noUpscale || !fitsTarget)
                {
                    // Оба размера задаются как ограничивающая область:
                    // сохраняем исходный aspect ratio и вписываем без обрезки.
                    im.Mutate(x=>x.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(ow, oh),
                        Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3
                    }));
                }
                string ext=Path.GetExtension(path).ToLowerInvariant();
                string outp = replaceOriginal
                    ? path
                    : Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+"_rresized"+ext);

                // При замене оригинала сначала сохраняем во временный файл,
                // чтобы не потерять исходник, если запись завершится с ошибкой.
                string savePath = replaceOriginal
                    ? Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileNameWithoutExtension(path) + "_rresizing_" + Guid.NewGuid().ToString("N") + ext)
                    : outp;

                try
                {
                    switch(ext)
                    {
                        case ".jpg":
                        case ".jpeg":
                            im.Save(savePath,new JpegEncoder{Quality=95});
                            break;
                        case ".png":
                            im.Save(savePath,new PngEncoder());
                            break;
                        case ".webp":
                            im.Save(savePath,new WebpEncoder{Quality=95});
                            break;
                        default:
                            im.Save(savePath);
                            break;
                    }

                    if(replaceOriginal)
                    {
                        File.Move(savePath, path, true);
                    }
                }
                finally
                {
                    if(replaceOriginal && File.Exists(savePath))
                    {
                        try { File.Delete(savePath); } catch { }
                    }
                }

                Interlocked.Increment(ref done);
            }catch(Exception ex){lock(errors)errors.Add(ex.Message);}
        });
        Status.Foreground=errors.Count==0?new SolidColorBrush(WpfColor.FromRgb(65,135,95)):error;
        Status.Text=errors.Count==0?$"Готово · {done} из {files.Length}":$"Готово · {done} из {files.Length} · ошибок: {errors.Count}";
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    const int GWL_STYLE = -16;
    const nint WS_CAPTION = 0x00C00000;
    const nint WS_THICKFRAME = 0x00040000;
    const nint WS_MAXIMIZEBOX = 0x00010000;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FLAGS = SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED;

    const uint WM_SYSCOMMAND = 0x0112;
    const nint SC_MINIMIZE = 0xF020;

    // Windows 11 DWM: use the native rounded-corner treatment.
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    nint WindowHandle => new WindowInteropHelper(this).Handle;
    void SendSystemCommand(nint command)
    {
        var hwnd = WindowHandle;
        if (hwnd != 0)
            SendMessage(hwnd, WM_SYSCOMMAND, command, 0);
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // Double-clicking the title bar deliberately does not maximize:
        // the window has no maximize mode anymore.
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            return;
        }

        try { DragMove(); } catch (InvalidOperationException) { }
    }

    void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SendSystemCommand(SC_MINIMIZE);
    }

    void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

}
