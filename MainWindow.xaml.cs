using Microsoft.Win32;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;
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

    void HideStatus()
    {
        Status.Text = "";
        Status.Visibility = Visibility.Collapsed;
    }

    private void Author_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://ryabov.bz",
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
        HideStatus();
    }

    void ShowError(string message)
    {
        Status.Visibility = Visibility.Visible;
        Status.Foreground = error;
        Status.FontSize = 17;
        Status.FontWeight = FontWeights.SemiBold;
        Status.Margin = new Thickness(0, 18, 0, 0);
        Status.Text = message;
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

    void ClearSize_Click(object sender, RoutedEventArgs e)
    {
        WidthBox.Clear();
        HeightBox.Clear();
        WidthBox.Focus();
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
        bool optimize = OptimizeCheck.IsChecked == true;
        if(!optimize && !int.TryParse(WidthBox.Text,out var w) && !int.TryParse(HeightBox.Text,out var h))
        {
            ShowError("Укажите ширину или высоту"); WidthBox.Focus(); return;
        }
        var d=new OpenFileDialog{Multiselect=true,Filter="Изображения|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff"};
        if(d.ShowDialog()==true)_=Process(d.FileNames);
    }

    async Task Process(string[] files)
    {
        int tw=int.TryParse(WidthBox.Text,out var w)?w:0;
        int th=int.TryParse(HeightBox.Text,out var h)?h:0;
        bool optimize = OptimizeCheck.IsChecked == true;
        if(tw<=0&&th<=0&&!optimize){ShowError("Укажите ширину или высоту");return;}

        // Все значения WPF считываем до Task.Run.
        bool noUpscale = NoUpscaleCheck.IsChecked == true;
        bool replaceOriginal = ReplaceOriginalCheck.IsChecked == true;

        Status.Visibility = Visibility.Visible;
        Status.Foreground=normal;
        Status.FontSize = 13;
        Status.FontWeight = FontWeights.Normal;
        Status.Margin = new Thickness(0, 18, 0, 0);
        Status.Text=$"Обрабатываем 1 из {files.Length}…";
        BatchProgress.Maximum = files.Length;
        BatchProgress.Value = 0;
        BatchProgress.Visibility = Visibility.Visible;

        int done=0;
        int processed=0;
        long originalBytes=0, outputBytes=0;
        var errors=new List<string>();

        await Task.Run(()=>
        {
            foreach(var path in files)
            {
                try
                {
                    long sourceLength = new FileInfo(path).Length;
                    Interlocked.Add(ref originalBytes, sourceLength);

                    using Image<Rgba32> im = Image.Load<Rgba32>(path);
					
					im.Mutate(x => x.AutoOrient());
					
                    int sw=im.Width, sh=im.Height;
                    bool resized = false;

                    if(tw>0 || th>0)
                    {
                        int ow,oh;
                        if(tw>0&&th>0){ow=tw;oh=th;}
                        else if(tw>0){ow=tw;oh=Math.Max(1,(int)Math.Round(sh*(double)tw/sw));}
                        else {oh=th;ow=Math.Max(1,(int)Math.Round(sw*(double)th/sh));}

                        bool fitsTarget = tw>0&&th>0
                            ? sw<=tw && sh<=th
                            : tw>0
                                ? sw<=tw
                                : sh<=th;

                        if(!noUpscale || !fitsTarget)
                        {
                            im.Mutate(x=>x.Resize(new ResizeOptions
                            {
                                Size = new SixLabors.ImageSharp.Size(ow, oh),
                                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                                Sampler = KnownResamplers.Lanczos3
                            }));
                            resized = im.Width != sw || im.Height != sh;
                        }
                    }

                    string ext=Path.GetExtension(path).ToLowerInvariant();
                    string outp = replaceOriginal
                        ? path
                        : Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+"_rresized"+ext);

                    string savePath = replaceOriginal
                        ? Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileNameWithoutExtension(path) + "_rresizing_" + Guid.NewGuid().ToString("N") + ext)
                        : outp;

                    try
                    {
                        if(optimize && (ext==".jpg" || ext==".jpeg" || ext==".webp" || ext==".png"))
                        {
                            OptimizeAndSave(im, ext, savePath, path, sourceLength, resized);
                        }
                        else
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
                        }

                        if(replaceOriginal)
                            File.Move(savePath, path, true);

                        long finalLength = new FileInfo(replaceOriginal ? path : savePath).Length;
                        Interlocked.Add(ref outputBytes, finalLength);
                    }
                    finally
                    {
                        if(replaceOriginal && File.Exists(savePath))
                        {
                            try { File.Delete(savePath); } catch { }
                        }
                    }

                    Interlocked.Increment(ref done);
                }
                catch(Exception ex)
                {
                    lock(errors) errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
                finally
                {
                    int current = Interlocked.Increment(ref processed);
                    Dispatcher.Invoke(() =>
                    {
                        BatchProgress.Value = current;
                        Status.Visibility = Visibility.Visible;
                        Status.Foreground = normal;
                        Status.FontSize = 13;
                        Status.Margin = new Thickness(0, 18, 0, 0);
                        Status.FontWeight = FontWeights.Normal;
                        Status.Text = $"Обрабатываем {current} из {files.Length}…";
                    });
                }
            }
        });

        BatchProgress.Value = files.Length;
        BatchProgress.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        Status.Foreground=errors.Count==0?new SolidColorBrush(WpfColor.FromRgb(65,135,95)):error;
        Status.FontSize = 17;
        Status.Margin = new Thickness(0, 18, 0, 0);
        Status.FontWeight = FontWeights.SemiBold;
        if(errors.Count==0 && optimize && originalBytes>0)
        {
            long saved=Math.Max(0,originalBytes-outputBytes);
            double percent=100.0*saved/originalBytes;
            Status.Text=replaceOriginal
                ? $"Готово · {done} из {files.Length} · −{FormatBytes(saved)} ({percent:0.0}%)\nОригинальные файлы заменены"
                : $"Готово · {done} из {files.Length} · −{FormatBytes(saved)} ({percent:0.0}%)\nНовые файлы сохранены рядом с оригиналами";
        }
        else
        {
            Status.Text=errors.Count==0
                ? (replaceOriginal
                    ? $"Готово · {done} из {files.Length}\nОригинальные файлы заменены"
                    : $"Готово · {done} из {files.Length}\nНовые файлы сохранены рядом с оригиналами")
                : $"Готово · {done} из {files.Length} · ошибок: {errors.Count}";
        }
    }

    static void OptimizeAndSave(Image<Rgba32> image, string ext, string destination, string originalPath, long sourceLength, bool resized)
    {
        // Метаданные камеры/геолокации и служебные профили удаляем перед сохранением.
        // ICC-профиль цвета намеренно сохраняем.
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;

        string tempDir = Path.Combine(Path.GetTempPath(), "RResizer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            if(ext==".png")
            {
                string candidate = Path.Combine(tempDir, "candidate.png");
                image.Save(candidate, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression
                });

                long candidateLength = new FileInfo(candidate).Length;
                if(resized || candidateLength < sourceLength)
                    File.Copy(candidate, destination, true);
                else
                    File.Copy(originalPath, destination, true);
                return;
            }

            bool webp = ext==".webp";
            string tempExt = webp ? ".webp" : ".jpg";
            string baseName = Path.Combine(tempDir, "candidate");
            int[] qualities = webp
                ? new[] { 92, 88, 84, 80, 76, 72, 68, 64, 60 }
                : new[] { 94, 90, 86, 82, 78, 74, 70, 66, 62, 58 };

            string? bestPath = null;
            long bestLength = long.MaxValue;

            foreach(int quality in qualities)
            {
                string candidate = baseName + quality + tempExt;
                if(webp)
                    image.Save(candidate, new WebpEncoder { Quality=quality });
                else
                    image.Save(candidate, new JpegEncoder { Quality=quality });

                long len = new FileInfo(candidate).Length;
                bool smaller = resized || len < sourceLength;
                bool acceptable = IsVisuallyClose(image, candidate);

                if(smaller && acceptable && len < bestLength)
                {
                    bestLength = len;
                    bestPath = candidate;
                }
            }

            // Если сжатие не даёт приемлемого выигрыша, сохраняем с высоким качеством.
            if(bestPath == null)
            {
                if(resized)
                {
                    if(webp)
                        image.Save(destination, new WebpEncoder { Quality=92 });
                    else
                        image.Save(destination, new JpegEncoder { Quality=94 });
                }
                else
                {
                    File.Copy(originalPath, destination, true);
                }
            }
            else
            {
                File.Copy(bestPath, destination, true);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    static bool IsVisuallyClose(Image<Rgba32> source, string candidatePath)
    {
        using Image<Rgba32> candidate = Image.Load<Rgba32>(candidatePath);
        if(candidate.Width != source.Width || candidate.Height != source.Height)
            return false;

        const int samples = 160;
        double sumAbs = 0;
        double sumLumA = 0, sumLumB = 0, sumLumASq = 0, sumLumBSq = 0, sumLumAB = 0;
        int count = 0;

        int stepX = Math.Max(1, source.Width / samples);
        int stepY = Math.Max(1, source.Height / samples);
        for(int y=0; y<source.Height; y+=stepY)
        for(int x=0; x<source.Width; x+=stepX)
        {
            Rgba32 a=source[x,y], b=candidate[x,y];
            sumAbs += (Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B))/3.0/255.0;

            double la=0.2126*a.R+0.7152*a.G+0.0722*a.B;
            double lb=0.2126*b.R+0.7152*b.G+0.0722*b.B;
            sumLumA+=la; sumLumB+=lb;
            sumLumASq+=la*la; sumLumBSq+=lb*lb; sumLumAB+=la*lb;
            count++;
        }

        double mae=sumAbs/count;
        double meanA=sumLumA/count, meanB=sumLumB/count;
        double varA=Math.Max(0,(sumLumASq/count)-meanA*meanA);
        double varB=Math.Max(0,(sumLumBSq/count)-meanB*meanB);
        double cov=(sumLumAB/count)-meanA*meanB;
        const double c1=6.5025, c2=58.5225;
        double ssim=((2*meanA*meanB+c1)*(2*cov+c2))/((meanA*meanA+meanB*meanB+c1)*(varA+varB+c2));

        // Внутренний критерий: небольшая средняя ошибка цвета + высокая структурная схожесть.
        return mae <= 0.0125 && ssim >= 0.985;
    }

    static string FormatBytes(long bytes)
    {
        string[] units={"Б","КБ","МБ","ГБ"};
        double value=bytes;
        int unit=0;
        while(value>=1024 && unit<units.Length-1){value/=1024;unit++;}
        return unit==0?$"{value:0} {units[unit]}":$"{value:0.##} {units[unit]}";
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
		var hwnd = WindowHandle;

		if (hwnd == 0)
			return;

		var style = GetWindowLongPtr(hwnd, GWL_STYLE);

		// Временно добавляем стандартную рамку Windows,
		// чтобы DWM применил свою анимацию сворачивания.
		if ((style & WS_CAPTION) == 0)
		{
			SetWindowLongPtr(
				hwnd,
				GWL_STYLE,
				style | WS_CAPTION | WS_THICKFRAME);

			SetWindowPos(
				hwnd,
				0,
				0, 0, 0, 0,
				SWP_FLAGS);
		}

		SendSystemCommand(SC_MINIMIZE);
	}

    void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

}
