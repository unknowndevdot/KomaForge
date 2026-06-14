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
    // 내보내기 중에는 디스패처를 펌프하므로(동영상 첫 프레임 캡처 대기), 메뉴 재클릭에 의한 재진입을 막는다.
    private bool _exporting;
    // 동영상 프레임 캡처로 디스패처를 펌프하는 동안엔 히스토리 타이머 등 콜백을 잠시 멈춘다(부분 상태 캡처 방지).
    private bool _pumpingMedia;

    private void ExportPagesAsImages_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0)
        {
            UpdateStatus("내보낼 페이지가 없습니다.");
            return;
        }

        if (_exporting)
        {
            return;
        }

        // 현재 편집 중인 페이지 상태를 먼저 저장한다.
        SaveCurrentPageState();

        // 내보내기 설정 창(목표 해상도 등). 취소하면 중단.
        var options = ShowExportDialog(_pageWidth, _pageHeight);
        if (options == null)
        {
            return;
        }

        var (scaleX, scaleY) = options.Value;

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
            _exporting = true;
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

                var bitmap = RenderPageToBitmap(scaleX, scaleY);

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
            _exporting = false;
        }

        if (exported > 0)
        {
            UpdateStatus($"{exported}개 페이지를 이미지로 내보냈습니다: {folder}");
        }
    }

    // 내보내기 설정 창. 배수(게이지)를 받아 (가로배율, 세로배율)을 돌려준다. 취소하면 null.
    // 페이지마다 크기가 달라 절대 해상도가 아닌 '원본 크기 × 배수'(균일 배율)로 내보낸다.
    // 세로 스택 레이아웃이라 이후 항목(포맷·배경·여백 등)을 아래에 덧붙이기 쉽다.
    private (double ScaleX, double ScaleY)? ShowExportDialog(double refW, double refH)
    {
        var nativeW = Math.Max(1, (int)Math.Round(refW));
        var nativeH = Math.Max(1, (int)Math.Round(refH));
        (double ScaleX, double ScaleY)? result = null;

        var dialog = new Window
        {
            Title = "이미지로 내보내기",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        var dark = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
        var gray = new SolidColorBrush(Color.FromRgb(0x77, 0x72, 0x68));
        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "배수", FontWeight = FontWeights.Bold, Foreground = dark, Margin = new Thickness(0, 0, 0, 6)
        });
        root.Children.Add(new TextBlock
        {
            Text = "원본 페이지 크기 × 배수로 내보냅니다. 페이지마다 크기가 달라도 같은 배수가 적용됩니다.",
            Foreground = gray, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap
        });

        var valueLabel = new TextBlock
        {
            Foreground = dark, FontWeight = FontWeights.Bold, MinWidth = 52,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var slider = new Slider
        {
            Minimum = 1, Maximum = 4, Value = 1,
            TickFrequency = 0.25, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        var sizeHint = new TextBlock { Foreground = gray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        void Refresh()
        {
            var s = slider.Value;
            valueLabel.Text = $"{s:0.##}×";
            sizeHint.Text = $"현재 페이지: {(int)Math.Round(nativeW * s)} × {(int)Math.Round(nativeH * s)}px";
        }

        slider.ValueChanged += (_, _) => Refresh();
        Refresh();

        var sliderRow = new DockPanel(); // 오른쪽=배수 값, 나머지 영역=슬라이더.
        DockPanel.SetDock(valueLabel, Dock.Right);
        sliderRow.Children.Add(valueLabel);
        sliderRow.Children.Add(slider);
        root.Children.Add(sliderRow);
        root.Children.Add(sizeHint);

        var cancelBtn = new Button { Content = "취소", MinWidth = 72 };
        cancelBtn.Click += (_, _) => dialog.Close();
        var okBtn = new Button { Content = "내보내기", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        okBtn.Click += (_, _) =>
        {
            var s = slider.Value;
            result = (s, s); // 균일 배수.
            dialog.DialogResult = true;
        };

        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(cancelBtn, Dock.Right);
        DockPanel.SetDock(okBtn, Dock.Right);
        bottom.Children.Add(cancelBtn);
        bottom.Children.Add(okBtn);
        root.Children.Add(bottom);

        dialog.Content = root;
        dialog.ShowDialog();
        return result;
    }

    // 현재 페이지를 비트맵으로 렌더한다. scaleX/scaleY로 출력 해상도를 키운다(1=페이지 픽셀 그대로, 슈퍼샘플링).
    private RenderTargetBitmap RenderPageToBitmap(double scaleX, double scaleY)
    {
        // 매우 큰 페이지 × 배수가 메모리를 폭발시키지 않도록 출력 한 변을 16000px로 제한한다.
        const int maxDim = 16000;
        var width = Math.Clamp((int)Math.Ceiling(_pageWidth * scaleX), 1, maxDim);
        var height = Math.Clamp((int)Math.Ceiling(_pageHeight * scaleY), 1, maxDim);
        var pageRect = new Rect(0, 0, _pageWidth, _pageHeight);

        // 뷰포트 컬링으로 화면 밖 칸이 Collapsed일 수 있으므로, 내보내기 전 전부 보이게 하고 레이아웃을 갱신한다.
        foreach (var panel in _panels)
        {
            panel.Frame.Visibility = Visibility.Visible;
        }
        PanelCanvas.UpdateLayout();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // 배경을 직접 그린다(페이지별 검/흰). PageSurface는 PageFrame 테두리(1px)만큼 오프셋이 있어
            // VisualBrush로 쓰면 좌/상에 1px 투명 여백이 생기므로, 그리드 원점(0,0)에 있는 PanelCanvas를 렌더한다.
            var background = new SolidColorBrush(CurrentPageBackgroundColor());
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

        // 논리 페이지 좌표를 '실제 출력 버퍼 크기'로 확대 → 벡터·이미지가 더 높은 해상도로 재래스터화(슈퍼샘플링).
        // 버퍼에서 역산한 실효 배율을 써서 클램프가 걸려도 내용이 잘리지 않고 정확히 채워지게 한다.
        var effScaleX = width / _pageWidth;
        var effScaleY = height / _pageHeight;
        visual.Transform = new ScaleTransform(effScaleX, effScaleY);

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

                // 내보내기는 최종 화질 우선: 실제 첫 프레임을 원본 해상도로 캡처하고, 실패 시 셸 썸네일로 폴백.
                var resolved = ResolveProjectPath(image.Path);
                var still = CaptureVideoFirstFrame(resolved) ?? GetVideoStillFrame(resolved);
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

    // 동영상의 '실제 첫 프레임'을 원본 해상도로 캡처한다(내보내기 최종 화질용). 실패하면 null.
    // MediaPlayer로 0초 프레임을 표시(ScrubbingEnabled)한 뒤 DrawVideo로 RenderTargetBitmap에 그린다.
    // 미디어 이벤트/디코드는 비동기라, 짧게 디스패처를 펌프하며 준비를 기다린다(내보내기는 1회성이라 허용).
    private BitmapSource? CaptureVideoFirstFrame(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        MediaPlayer? player = null;
        try
        {
            _pumpingMedia = true; // 펌프 동안 히스토리 캡처 등 타이머 콜백이 끼어들지 않게 한다.
            player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            var opened = false;
            var failed = false;
            player.MediaOpened += (_, _) => opened = true;
            player.MediaFailed += (_, _) => failed = true;
            player.Open(new Uri(path, UriKind.Absolute));

            // MediaOpened까지 대기(자연 해상도 확보). 최대 ~3초.
            if (!PumpUntil(() => opened || failed, 3000) || failed
                || player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
            {
                return null;
            }

            // 0초 프레임을 표시: 재생 후 즉시 일시정지하면 ScrubbingEnabled로 그 프레임이 렌더된다.
            player.Position = TimeSpan.Zero;
            player.Play();
            player.Pause();

            // 프레임 디코드/표시를 기다린다(전용 이벤트가 없어 짧게 펌프).
            PumpUntil(() => false, 400);

            var w = player.NaturalVideoWidth;
            var h = player.NaturalVideoHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawVideo(player, new Rect(0, 0, w, h));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { player?.Close(); } catch { /* 정리 실패는 무시 */ }
            _pumpingMedia = false;
        }
    }

    // 조건이 참이 되거나 제한시간(ms)이 지날 때까지 디스패처를 펌프한다. 조건 충족 시 true.
    private static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var elapsed = 0;
        const int step = 15;
        while (!condition() && elapsed < timeoutMs)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(step); // 미디어 스레드가 디코드를 진행할 시간을 준다.
            elapsed += step;
        }

        return condition();
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
            ReplacePages(project.Pages);
            _currentPageIndex = Math.Clamp(project.CurrentPageIndex, 0, _pages.Count - 1);
            LoadPage(_pages[_currentPageIndex]);
            UpdatePageList();
            // 불러온 프로젝트로 히스토리 기준선을 새로 잡고 이전 기록을 비운다(프로젝트 간 실행취소 방지·재사용 정합성).
            ResetHistoryBaseline();
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateUndoRedoButtons();
            UpdateStatus("프로젝트를 불러왔습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트를 불러올 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
