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
    private void ExportPagesAsImages_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0)
        {
            UpdateStatus("내보낼 페이지가 없습니다.");
            return;
        }

        // 현재 편집 중인 페이지 상태를 먼저 저장한다.
        SaveCurrentPageState();

        var dialog = new OpenFolderDialog
        {
            Title = "페이지 이미지를 저장할 폴더 선택"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var originalIndex = _currentPageIndex;
        var exported = 0;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            ClearSelection();

            for (var i = 0; i < _pages.Count; i++)
            {
                _currentPageIndex = i;
                LoadPage(_pages[i]);
                ClearSelection();             // 선택 UI(핸들/박스)가 결과에 안 나오도록.
                PageSurface.UpdateLayout();

                // 동영상은 RenderTargetBitmap이 못 잡으므로 첫 프레임 스틸을 임시로 얹는다.
                // (움직이는 gif/webp는 페이지를 새로 로드하므로 자연히 첫 프레임 상태다.)
                var stills = AddVideoStillsForExport();
                PageSurface.UpdateLayout();

                var bitmap = RenderPageToBitmap();

                foreach (var (layer, temp) in stills)
                {
                    layer.Children.Remove(temp);
                }

                var fileName = $"{i + 1:D3}_{SanitizeFileName(_pages[i].Name)}.png";
                var path = System.IO.Path.Combine(folder, fileName);
                SavePng(bitmap, path);
                exported++;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"이미지를 내보내지 못했습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 원래 보던 페이지로 복원.
            _currentPageIndex = Math.Clamp(originalIndex, 0, _pages.Count - 1);
            LoadPage(_pages[_currentPageIndex]);
            Mouse.OverrideCursor = null;
        }

        if (exported > 0)
        {
            UpdateStatus($"{exported}개 페이지를 이미지로 내보냈습니다: {folder}");
        }
    }

    // 현재 페이지를 페이지 크기 그대로 비트맵으로 렌더한다.
    private RenderTargetBitmap RenderPageToBitmap()
    {
        var width = (int)Math.Ceiling(_pageWidth);
        var height = (int)Math.Ceiling(_pageHeight);
        var pageRect = new Rect(0, 0, _pageWidth, _pageHeight);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // 배경을 직접 그린다(페이지별 검/흰). PageSurface는 PageFrame 테두리(1px)만큼 오프셋이 있어
            // VisualBrush로 쓰면 좌/상에 1px 투명 여백이 생기므로, 그리드 원점(0,0)에 있는 PanelCanvas를 렌더한다.
            var background = BlackBackgroundCheckBox?.IsChecked == true ? Brushes.Black : Brushes.White;
            context.DrawRectangle(background, null, pageRect);

            // 콘텐츠(칸). PanelCanvas는 PageOverlay(선택 UI)와 분리돼 있어 핸들/선택박스가 자동 제외된다.
            var brush = new VisualBrush(PanelCanvas)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = pageRect,
                Stretch = Stretch.Fill
            };
            context.DrawRectangle(brush, null, pageRect);
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // 현재 페이지의 동영상마다 첫 프레임 스틸 이미지를 레이어에 임시로 얹는다(내보내기 후 제거).
    private List<(Grid Layer, Image Temp)> AddVideoStillsForExport()
    {
        var temps = new List<(Grid, Image)>();

        foreach (var panel in _panels)
        {
            foreach (var image in panel.Images)
            {
                if (image.Kind != MediaKind.Video)
                {
                    continue;
                }

                var still = GetVideoStillFrame(ResolveProjectPath(image.Path));
                if (still == null)
                {
                    continue;
                }

                var temp = new Image
                {
                    Source = still,
                    Stretch = Stretch.Uniform,
                    Width = image.Content.Width,
                    Height = image.Content.Height,
                    RenderTransform = image.Content.RenderTransform, // 동영상과 같은 변환(확대/이동) 공유.
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(temp, BitmapScalingMode.HighQuality);

                // 선택 테두리(마지막 자식)보다 아래, 동영상 위에 끼워 넣는다.
                var insertIndex = Math.Max(0, image.Layer.Children.Count - 1);
                image.Layer.Children.Insert(insertIndex, temp);
                temps.Add((image.Layer, temp));
            }
        }

        return temps;
    }

    // 동영상의 첫 프레임(포스터)을 Windows 셸 썸네일로 얻는다. 실패하면 null.
    private static BitmapSource? GetVideoStillFrame(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var guid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory);
            var size = new ShellSize { cx = 1024, cy = 1024 };
            factory.GetImage(size, SIIGBF_BIGGERSIZEOK, out var hBitmap);
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellSize
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage([In] ShellSize size, [In] int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string SanitizeFileName(string name)
    {
        var cleaned = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars()));
        cleaned = cleaned.Trim();
        return string.IsNullOrEmpty(cleaned) ? "page" : cleaned;
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveProjectAs();
    }

    // 메뉴 [파일 > 저장]: Ctrl+S와 동일하게 현재 파일에 덮어쓰기 저장.
    private void SaveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SaveProjectToCurrentOrPrompt();
    }

    // 메뉴 [파일 > 종료].
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // Ctrl+S: 현재 불러왔거나 저장한 파일에 덮어쓰기 저장. 경로가 없으면 다른 이름으로 저장 대화상자.
    private void SaveProjectToCurrentOrPrompt()
    {
        if (!string.IsNullOrWhiteSpace(_projectFilePath))
        {
            SaveProjectToFile(_projectFilePath!);
            return;
        }

        SaveProjectAs();
    }

    private void SaveProjectAs()
    {
        SaveCurrentPageState();

        var dialog = new SaveFileDialog
        {
            Title = "프로젝트 저장",
            Filter = "KomaForge 프로젝트 (*.kfjson)|*.kfjson|JSON 파일 (*.json)|*.json",
            FileName = string.IsNullOrWhiteSpace(_projectFilePath)
                ? GetDefaultProjectFileName()
                : Path.GetFileName(_projectFilePath)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SaveProjectToFile(dialog.FileName);
    }

    private void SaveProjectToFile(string fileName)
    {
        SaveCurrentPageState();

        var project = new ComicProjectData
        {
            Title = ComicTitleTextBox.Text.Trim(),
            AutoMargin = ParseDoubleOr(AutoMarginTextBox.Text, 24),
            AutoGutter = ParseDoubleOr(AutoGutterTextBox.Text, 14),
            CurrentPageIndex = _currentPageIndex,
            Pages = CaptureProjectPages(Path.GetDirectoryName(fileName))
        };

        try
        {
            var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(fileName, json);
            _projectFilePath = fileName;
            _projectBaseDirectory = Path.GetDirectoryName(fileName);
            UpdateStatus("프로젝트를 저장했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트를 저장할 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "프로젝트 불러오기",
            Filter = "KomaForge 프로젝트 (*.kfjson;*.nvjson;*.json)|*.kfjson;*.nvjson;*.json|모든 파일 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var project = JsonSerializer.Deserialize<ComicProjectData>(json);
            if (project == null || project.Pages.Count == 0)
            {
                UpdateStatus("불러올 페이지가 없습니다.");
                return;
            }

            _projectBaseDirectory = Path.GetDirectoryName(dialog.FileName);
            _projectFilePath = dialog.FileName;
            ComicTitleTextBox.Text = project.Title;
            AutoMarginTextBox.Text = $"{project.AutoMargin:0}";
            AutoGutterTextBox.Text = $"{project.AutoGutter:0}";
            _pages.Clear();
            _pages.AddRange(project.Pages);
            _currentPageIndex = Math.Clamp(project.CurrentPageIndex, 0, _pages.Count - 1);
            LoadPage(_pages[_currentPageIndex]);
            UpdatePageList();
            UpdateStatus("프로젝트를 불러왔습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트를 불러올 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
