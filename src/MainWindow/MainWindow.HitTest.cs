using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using SkiaSharp;

namespace KomaForge;

public partial class MainWindow : Window
{
    // 칸 선택 히트 영역을 프레임보다 바깥으로 조금 넓힌다(테두리를 노리다 살짝 빗나가도 칸이 선택되게).
    private const double PanelOutwardHitMargin = 8;

    private static bool IsOnPanelBorder(ComicPanel panel, Point point)
    {
        const double borderHitSize = 18;
        const double outward = PanelOutwardHitMargin;
        var width = panel.Frame.ActualWidth;
        var height = panel.Frame.ActualHeight;

        // 프레임 바깥이라도 outward 이내면 테두리로 인정한다. 그보다 더 밖이면 테두리가 아니다
        // (넘친 이미지 위 클릭이 칸 테두리로 오인되지 않도록 범위를 제한).
        if (point.X < -outward || point.Y < -outward || point.X > width + outward || point.Y > height + outward)
        {
            return false;
        }

        // 프레임 밖(바깥 밴드)이거나 안쪽 테두리 밴드이면 테두리로 본다.
        return point.X < 0 || point.Y < 0 || point.X > width || point.Y > height ||
               point.X <= borderHitSize ||
               point.Y <= borderHitSize ||
               point.X >= width - borderHitSize ||
               point.Y >= height - borderHitSize;
    }

    private static PanelImage? FindImageAtPoint(ComicPanel panel, Point panelPoint, bool includeLocked = false)
    {
        // 실제 화면 z-순서: 크롭 OFF 이미지(FreeImageCanvas)가 크롭 ON 이미지(ImageCanvas)보다 항상 앞에 있다.
        // 각 그룹 안에서는 panel.Images의 뒤쪽(높은 인덱스)이 위에 온다.
        // 따라서 크롭 OFF 그룹을 위에서부터 먼저 보고, 없으면 크롭 ON 그룹을 본다.
        return FindImageAtPointInGroup(panel, panelPoint, includeLocked, cropped: false)
            ?? FindImageAtPointInGroup(panel, panelPoint, includeLocked, cropped: true);
    }

    private static PanelImage? FindImageAtPointInGroup(ComicPanel panel, Point panelPoint, bool includeLocked, bool cropped)
    {
        for (var index = panel.Images.Count - 1; index >= 0; index--)
        {
            var image = panel.Images[index];
            if (image.IsCropped != cropped)
            {
                continue;
            }

            if (image.IsLocked && !includeLocked)
            {
                continue;
            }

            if (IsOpaqueImagePixelAtPoint(image, panelPoint))
            {
                return image;
            }
        }

        return null;
    }

    private static bool IsOpaqueImagePixelAtPoint(PanelImage image, Point panelPoint)
    {
        // 크롭 ON 이미지는 칸 사변형 밖에서는 화면에 잘려 보이지 않으므로 클릭 대상이 아니다.
        // (확대해 칸 밖으로 넘친 부분이 클릭을 가로채던 문제를 방지한다.)
        // 사변형 기하는 UpdatePanelShape가 항상 최신으로 유지하는 QuadFill.Data를 재사용한다(매번 새로 만들지 않음).
        if (image.IsCropped)
        {
            var clip = image.OwnerPanel.QuadFill.Data ?? CreatePanelQuadGeometry(image.OwnerPanel);
            if (!clip.FillContains(panelPoint))
            {
                return false;
            }
        }

        var content = image.Content;
        var transform = content.TransformToAncestor(image.OwnerPanel.Frame);
        var inverse = transform.Inverse;
        if (inverse == null)
        {
            return false;
        }

        var imagePoint = inverse.Transform(panelPoint);
        var controlWidth = content.ActualWidth > 0 ? content.ActualWidth : content.Width;
        var controlHeight = content.ActualHeight > 0 ? content.ActualHeight : content.Height;

        if (imagePoint.X < 0 || imagePoint.Y < 0 || imagePoint.X > controlWidth || imagePoint.Y > controlHeight)
        {
            return false;
        }

        // 동영상 등 비트맵이 없는 경우엔 사각형(컨트롤 영역) 기준으로 판정한다.
        var bitmap = GetAlphaBitmap(image);
        if (bitmap == null)
        {
            return true;
        }

        var scale = Math.Min(controlWidth / bitmap.PixelWidth, controlHeight / bitmap.PixelHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return false;
        }

        var drawnWidth = bitmap.PixelWidth * scale;
        var drawnHeight = bitmap.PixelHeight * scale;
        var offsetX = (controlWidth - drawnWidth) / 2;
        var offsetY = (controlHeight - drawnHeight) / 2;

        if (imagePoint.X < offsetX ||
            imagePoint.Y < offsetY ||
            imagePoint.X > offsetX + drawnWidth ||
            imagePoint.Y > offsetY + drawnHeight)
        {
            return false;
        }

        var pixelX = (int)Math.Clamp((imagePoint.X - offsetX) / scale, 0, bitmap.PixelWidth - 1);
        var pixelY = (int)Math.Clamp((imagePoint.Y - offsetY) / scale, 0, bitmap.PixelHeight - 1);
        return GetPixelAlpha(bitmap, pixelX, pixelY) > 8;
    }

    // 픽셀 알파를 읽기 위한 BGRA 변환본을 이미지별로 캐시한다(매 히트테스트마다 변환 비용 제거).
    // 소스가 바뀌면(애니/동영상 프레임 교체) Key 불일치로 자동 재생성된다.
    private static BitmapSource? GetAlphaBitmap(PanelImage image)
    {
        if (image.Image?.Source is not BitmapSource src)
        {
            return null;
        }

        if (ReferenceEquals(image.AlphaCacheKey, src) && image.AlphaCacheValue != null)
        {
            return image.AlphaCacheValue;
        }

        BitmapSource converted = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        if (converted.CanFreeze && !converted.IsFrozen)
        {
            converted.Freeze();
        }

        image.AlphaCacheKey = src;
        image.AlphaCacheValue = converted;
        return converted;
    }

    private static byte GetPixelAlpha(BitmapSource bgra, int x, int y)
    {
        // bgra는 GetAlphaBitmap이 BGRA로 보장한 비트맵이다.
        var pixels = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return pixels[3];
    }

    private static bool TryGetDroppedImagePaths(DragEventArgs e, out List<string> paths)
    {
        paths = new List<string>();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        paths = files.Where(IsSupportedImagePath).ToList();
        return paths.Count > 0;
    }

    private static bool IsSupportedImagePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"
            or ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv" or ".m4v";
    }

    private static bool IsInsideResizeHandle(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsInsideBubble(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Border border && Equals(border.Tag, "SpeechBubble"))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static double GetCanvasLeft(FrameworkElement element)
    {
        var value = Canvas.GetLeft(element);
        return double.IsNaN(value) ? 0 : value;
    }

    private static double GetCanvasTop(FrameworkElement element)
    {
        var value = Canvas.GetTop(element);
        return double.IsNaN(value) ? 0 : value;
    }

    // 칸이 페이지 밖으로 넘어갈 수 있게 허용하되(넘어간 부분은 캔버스 클리핑으로 잘림),
    // 최소 MinPanelVisible 만큼은 페이지 안에 남겨 다시 잡을 수 있게 한다.
    private const double MinPanelVisible = 40;

    private double ClampPanelX(double x, double width)
    {
        return Math.Clamp(x, MinPanelVisible - width, _pageWidth - MinPanelVisible);
    }

    private double ClampPanelY(double y, double height)
    {
        return Math.Clamp(y, MinPanelVisible - height, _pageHeight - MinPanelVisible);
    }

    private static List<int> ParsePattern(string text)
    {
        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var count) ? count : 0)
            .Where(count => count > 0 && count <= 6)
            .ToList();
    }

    private static double ParseDoubleOr(string text, double fallback)
    {
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // 상대 경로 해석: 수동 불러오기면 저장 파일 폴더를 먼저, 그 다음 실행 파일 폴더 기준으로 찾는다.
    // (자동저장 복원 시에는 _projectBaseDirectory가 없어 실행 파일 폴더만 본다.) 절대 경로는 그대로 쓴다.
    private string ResolveProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
        {
            return path;
        }

        string? firstCandidate = null;
        foreach (var baseDirectory in EnumerateResolveBaseDirectories())
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, path));
            firstCandidate ??= candidate;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return firstCandidate!; // 어느 쪽에도 없으면 첫 후보(존재 확인은 호출부에서).
    }

    private IEnumerable<string> EnumerateResolveBaseDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_projectBaseDirectory))
        {
            yield return _projectBaseDirectory!;
        }

        yield return AppContext.BaseDirectory;
    }

    // 저장용 경로: 기준 폴더(또는 하위)면 그 기준 상대 경로, 그 외엔 절대 경로.
    // 기준 폴더 — 자동 저장(projectDirectory == null)은 실행 파일 폴더,
    //             수동 저장은 저장 파일 폴더로 판단한다.
    private static string MakeStorablePath(string path, string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return path; // 이미 상대거나 비어 있음.
        }

        var baseDirectory = string.IsNullOrWhiteSpace(projectDirectory)
            ? AppContext.BaseDirectory
            : projectDirectory!;

        return TryMakeRelativeUnder(baseDirectory, path) ?? path;
    }

    // fullPath가 baseDirectory 또는 그 하위면 상대 경로를, 아니면 null을 반환한다.
    private static string? TryMakeRelativeUnder(string baseDirectory, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(baseDirectory, fullPath);
            // baseDirectory 밖이면 ".."로 시작하거나 절대 경로가 된다 → 상대화하지 않는다.
            if (Path.IsPathFullyQualified(relative) ||
                relative.StartsWith("..", StringComparison.Ordinal))
            {
                return null;
            }

            return relative;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    // 불러오기 중에는 메뉴 IsChecked 세팅이 PageFitCheckBox_Changed를 통해 저장을 트리거하지 않도록 막는다.
    private bool _loadingWindowSettings;

    private void LoadWindowSettings()
    {
        _loadingWindowSettings = true;
        try
        {
            // 새 이름이 있으면 그것을, 없으면 구버전 파일(window-settings.json)을 불러온다.
            var path = File.Exists(_windowSettingsPath)
                ? _windowSettingsPath
                : (File.Exists(_legacyWindowSettingsPath) ? _legacyWindowSettingsPath : null);
            if (path == null)
            {
                return;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<WindowSettings>(json);
            if (settings == null)
            {
                return;
            }

            Width = Math.Max(MinWidth, settings.Width);
            Height = Math.Max(MinHeight, settings.Height);

            // 저장된 영역이 화면에 충분히 보이면 그 위치로 복원(스냅된 위치·다중 모니터·음수 좌표 지원).
            if (IsRectMostlyOnScreen(settings.Left, settings.Top, settings.Width, settings.Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.Left;
                Top = settings.Top;
            }

            if (string.Equals(settings.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase))
            {
                WindowState = WindowState.Maximized;
            }

            // 앱 설정 복원
            LayoutPatternTextBox.Text = settings.LayoutPattern ?? "1,2,1";
            AutoMarginTextBox.Text = settings.AutoMargin ?? "24";
            AutoGutterTextBox.Text = settings.AutoGutter ?? "14";
            SetBubbleShapeByTag(settings.BubbleShape ?? "Oval");
            PageFitMenuItem.IsChecked = settings.PageFit;
            PageWidthFitMenuItem.IsChecked = settings.PageWidthFit;
            SetInspectorVisible(settings.InspectorVisible);
            _selectionPreviewEnabled = settings.SelectionPreview;
            _keepAspectRatio = settings.KeepAspectRatio;
            _autosaveDisabled = settings.AutosaveDisabled;
            _imageCacheLimitMb = settings.ImageCacheLimitMb < 0 ? 0 : settings.ImageCacheLimitMb;
            _exportScale = settings.ExportScale > 0 ? settings.ExportScale : 1;
            _exportWebp = settings.ExportWebp;
            _exportLossless = settings.ExportLossless;
            _exportQuality = settings.ExportQuality is >= 1 and <= 100 ? settings.ExportQuality : 90;
            _recentColors.Clear();
            if (settings.RecentColors != null)
            {
                _recentColors.AddRange(settings.RecentColors);
            }
            ImportShortcuts(settings.Shortcuts);
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        finally
        {
            _loadingWindowSettings = false;
        }
    }

    // 저장된 창 영역이 (다중 모니터 포함) 화면 안에 충분히 보이는지 — 완전히 화면 밖이면 복원하지 않는다.
    private static bool IsRectMostlyOnScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var win = new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
        win.Intersect(screen);
        return win.Width >= 80 && win.Height >= 80;
    }

    private void SetBubbleShapeByTag(string tag)
    {
        foreach (var item in BubbleShapeComboBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                BubbleShapeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void SaveWindowSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_windowSettingsPath)!);

            // 최대화는 RestoreBounds(복원 크기)를, 그 외(스냅 포함 일반 상태)는 실제 화면 영역을 저장한다.
            // RestoreBounds는 스냅된 실제 크기가 아니라 '스냅 해제 시 크기'라서 스냅 보존에는 쓰지 않는다.
            var maximized = WindowState == System.Windows.WindowState.Maximized;
            var bounds = maximized || double.IsNaN(Left) || double.IsNaN(Top)
                ? RestoreBounds
                : new Rect(Left, Top, ActualWidth, ActualHeight);

            var settings = new WindowSettings
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Left = bounds.Left,
                Top = bounds.Top,
                WindowState = maximized ? "Maximized" : "Normal",
                PageFit = PageFitMenuItem.IsChecked,
                PageWidthFit = PageWidthFitMenuItem.IsChecked,
                LayoutPattern = LayoutPatternTextBox.Text,
                AutoMargin = AutoMarginTextBox.Text,
                AutoGutter = AutoGutterTextBox.Text,
                BubbleShape = (BubbleShapeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Oval",
                InspectorVisible = InspectorPanel.Visibility == Visibility.Visible,
                SelectionPreview = _selectionPreviewEnabled,
                KeepAspectRatio = _keepAspectRatio,
                AutosaveDisabled = _autosaveDisabled,
                ImageCacheLimitMb = _imageCacheLimitMb,
                ExportScale = _exportScale,
                ExportWebp = _exportWebp,
                ExportLossless = _exportLossless,
                ExportQuality = _exportQuality,
                RecentColors = new List<string>(_recentColors),
                Shortcuts = ExportShortcuts()
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_windowSettingsPath, json);
        }
        catch
        {
            // Window size persistence is a convenience; the editor should still close normally.
        }
    }
}
