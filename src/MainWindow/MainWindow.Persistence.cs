using ImageMagick;
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

    private enum ExportFormat { Png, Webp, Script }

    // 마지막 내보내기 설정(세션·세션 간 기억). _exportWebp=null이면 형식은 페이지 움직임 유무로 자동 선택.
    // (투명 배경은 별도 옵션 없이 '페이지 배경색'을 투명으로 두면 그대로 투명 출력된다.)
    private double _exportScale = 1;
    private bool? _exportWebp;
    private bool _exportLossless = true;
    private int _exportQuality = 90;

    private sealed class ExportSettings
    {
        public ExportFormat Format;
        public double Scale = 1;
        public bool Lossless;
        public int Quality = 90;
    }

    private void ExportAllPages_Click(object sender, RoutedEventArgs e) => RunExport(currentPageOnly: false);
    private void ExportCurrentPage_Click(object sender, RoutedEventArgs e) => RunExport(currentPageOnly: true);

    // 진행률·취소 창. 동기 루프 중 Report로 갱신하며, 닫기/취소 버튼은 Cancelled를 세운다.
    private sealed class ExportProgress
    {
        public Window Window = null!;
        public ProgressBar Bar = null!;
        public TextBlock Label = null!;
        public bool Cancelled;

        // 진행 텍스트·막대 갱신 후 디스패처를 한 번 펌프해 창을 다시 그리고 취소 클릭을 처리한다.
        public void Report(string text, double fraction)
        {
            Label.Text = text;
            Bar.Value = Math.Clamp(fraction, 0, 1);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private ExportProgress CreateExportProgress(string title)
    {
        var p = new ExportProgress();
        var dark = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
        var root = new StackPanel { Margin = new Thickness(16), Width = 300 };
        p.Label = new TextBlock { Text = "준비 중…", Foreground = dark, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        p.Bar = new ProgressBar { Height = 16, Minimum = 0, Maximum = 1, Value = 0 };
        var cancel = new Button { Content = "취소", MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        cancel.Click += (_, _) => p.Cancelled = true;
        root.Children.Add(p.Label);
        root.Children.Add(p.Bar);
        root.Children.Add(cancel);

        p.Window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            Content = root
        };
        p.Window.Closing += (_, _) => p.Cancelled = true; // 창 닫기 = 취소.
        p.Window.Show();
        return p;
    }

    // 내보내기 공통 흐름. 형식(PNG 정지 / WebP 움직임)은 설정 창에서 고른다.
    // currentPageOnly=false면 전체 페이지(폴더에 페이지별 파일), true면 현재 페이지 한 파일.
    private void RunExport(bool currentPageOnly)
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

        SaveCurrentPageState();

        // 비주얼 노벨 모드의 '전체 페이지 내보내기'에서는 형식에 '스크립트(.txt)'를 추가로 제공한다.
        var allowScript = !currentPageOnly && _flow.Enabled;

        // 길이·FPS는 각 이미지의 '출력' 설정대로 자동 적용된다(설정 창에는 형식·배수·품질만).
        var settings = ShowExportDialog(allowScript);
        if (settings == null)
        {
            return;
        }

        if (settings.Format == ExportFormat.Script)
        {
            ExportScriptTxt();
            return;
        }

        var webp = settings.Format == ExportFormat.Webp;
        var ext = webp ? "webp" : "png";
        var originalIndex = _currentPageIndex;

        if (currentPageOnly)
        {
            var save = new SaveFileDialog
            {
                Title = "현재 페이지 내보내기",
                Filter = webp ? "WebP 이미지 (*.webp)|*.webp" : "PNG 이미지 (*.png)|*.png",
                FileName = $"{_currentPageIndex + 1:D3}_{SanitizeFileName(_pages[_currentPageIndex].Name)}.{ext}"
            };
            if (save.ShowDialog(this) != true)
            {
                return;
            }

            var progress = webp ? CreateExportProgress("내보내는 중…") : null; // PNG 한 장은 즉시라 진행창 생략.
            try
            {
                _exporting = true;
                Mouse.OverrideCursor = Cursors.Wait;
                ClearSelection();
                if (webp)
                {
                    WaitForVideosReady(); // 동영상 길이가 준비된 뒤 출력 길이를 계산.
                    var (dur, fps) = ComputeAnimationDefaults(); // 현재 페이지 이미지들의 출력 설정에서 자동 도출.
                    var frames = RenderCurrentPageAnimation(save.FileName, (settings.Scale, fps, dur, settings.Lossless, settings.Quality),
                        (f, total) => { progress!.Report($"프레임 {f + 1}/{total}", (double)(f + 1) / total); return !progress.Cancelled; });
                    UpdateStatus(frames < 0 ? "내보내기를 취소했습니다." : $"움직이는 WebP로 내보냈습니다 ({frames}프레임): {save.FileName}");
                }
                else
                {
                    ExportPageToPng(save.FileName, settings.Scale);
                    UpdateStatus($"이미지로 내보냈습니다: {save.FileName}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"내보내지 못했습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progress?.Window.Close();
                LoadPage(_pages[_currentPageIndex]); // 라이브 상태 복원(타이머·재생 재개).
                Mouse.OverrideCursor = null;
                _exporting = false;
            }

            return;
        }

        var dialog = new OpenFolderDialog { Title = "페이지를 저장할 폴더 선택" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var total = _pages.Count;
        var exported = 0;
        var failed = new List<string>();
        var cancelled = false;
        var progressAll = CreateExportProgress("내보내는 중…");
        try
        {
            _exporting = true;
            Mouse.OverrideCursor = Cursors.Wait;
            ClearSelection();

            for (var i = 0; i < total; i++)
            {
                if (progressAll.Cancelled)
                {
                    break;
                }

                progressAll.Report($"페이지 {i + 1}/{total}", (double)i / total);

                // 한 페이지 실패가 전체를 중단시키지 않도록 페이지 단위로 예외를 가둔다.
                try
                {
                    _currentPageIndex = i;
                    LoadPage(_pages[i]);
                    ClearSelection();
                    PageSurface.UpdateLayout();

                    var path = System.IO.Path.Combine(folder, $"{i + 1:D3}_{SanitizeFileName(_pages[i].Name)}.{ext}");
                    if (webp)
                    {
                        WaitForVideosReady(); // 페이지 로드 직후 동영상 길이가 준비되길 기다린 뒤 계산.
                        var (pageDur, pageFps) = ComputeAnimationDefaults(); // 각 페이지의 출력 설정대로.
                        var pageIndex = i;
                        var frames = RenderCurrentPageAnimation(path, (settings.Scale, pageFps, pageDur, settings.Lossless, settings.Quality),
                            (f, fc) => { progressAll.Report($"페이지 {pageIndex + 1}/{total} · 프레임 {f + 1}/{fc}", (pageIndex + (double)(f + 1) / fc) / total); return !progressAll.Cancelled; });
                        if (frames < 0)
                        {
                            break; // 취소(렌더 중단).
                        }
                    }
                    else
                    {
                        ExportPageToPng(path, settings.Scale);
                    }
                    exported++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{i + 1}. {_pages[i].Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            cancelled = progressAll.Cancelled; // 닫기 전에 실제 취소 상태를 캡처(Close가 Closing→Cancelled를 세움).
            progressAll.Window.Close();
            _currentPageIndex = Math.Clamp(originalIndex, 0, _pages.Count - 1);
            LoadPage(_pages[_currentPageIndex]);
            Mouse.OverrideCursor = null;
            _exporting = false;
        }

        if (cancelled)
        {
            UpdateStatus($"내보내기를 취소했습니다 ({exported}/{total} 완료): {folder}");
        }
        else if (failed.Count > 0)
        {
            UpdateStatus($"{exported}/{total} 페이지 완료, {failed.Count}개 실패: {folder}");
            MessageBox.Show(this, "다음 페이지를 내보내지 못했습니다:\n\n" + string.Join("\n", failed), "일부 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            UpdateStatus($"{exported}개 페이지를 내보냈습니다: {folder}");
        }
    }

    // 모든 페이지의 말풍선 텍스트(페이지 목록에 보이는 요약)를 한 줄씩 .txt로 내보낸다.
    private void ExportScriptTxt()
    {
        var save = new SaveFileDialog
        {
            Title = "스크립트 내보내기",
            Filter = "텍스트 (*.txt)|*.txt",
            FileName = $"{SanitizeFileName(string.IsNullOrWhiteSpace(ComicTitleTextBox.Text) ? "script" : ComicTitleTextBox.Text.Trim())}.txt"
        };
        if (save.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // 페이지마다 말풍선 텍스트를 순서대로 ': '로 이어 한 줄로(페이지 목록 요약과 동일).
            var lines = _pages.Select(PageScriptLine);
            System.IO.File.WriteAllText(save.FileName, string.Join("\r\n", lines), new System.Text.UTF8Encoding(true));
            UpdateStatus($"스크립트를 내보냈습니다: {save.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"스크립트를 내보내지 못했습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 한 페이지의 말풍선 텍스트를 순서대로(칸→말풍선) ': '로 이어 한 줄로 만든다(빈 말풍선 제외).
    private static string PageScriptLine(ComicPageData page)
    {
        var texts = new List<string>();
        foreach (var panel in page.Panels)
        {
            foreach (var bubble in panel.Bubbles)
            {
                var t = (bubble.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                if (t.Length > 0)
                {
                    texts.Add(t);
                }
            }
        }
        return string.Join(": ", texts);
    }

    // 현재 페이지를 PNG 한 장으로 저장(동영상은 첫 프레임 스틸을 임시 합성).
    private void ExportPageToPng(string path, double scale)
    {
        var stills = AddVideoStillsForExport();
        PageSurface.UpdateLayout();
        var bitmap = RenderPageToBitmap(scale, scale);
        foreach (var (layer, temp) in stills)
        {
            layer.Children.Remove(temp);
        }
        SavePng(bitmap, path);
    }

    // 현재 페이지에 움직이는 요소(애니/동영상)가 있는지 — 내보내기 형식 기본값 결정용.
    private bool HasMovingContent()
    {
        foreach (var panel in _panels)
        {
            foreach (var image in panel.Images)
            {
                if ((image.Kind == MediaKind.Animated && image.Player != null && image.Player.FrameCount > 1)
                    || image.Kind == MediaKind.Video)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // === 현재 페이지를 움직이는 WebP로 내보내기 ===

    private sealed class AnimExportContext
    {
        public Image Target = null!;
        public BitmapSource[] Frames = System.Array.Empty<BitmapSource>();
        public int[] Delays = System.Array.Empty<int>();
        public double Total;      // 원본 총 길이 ms.
        public double OutDurMs;   // 실효 출력 길이 ms.
        public bool Override;     // 출력 길이를 지정했는가(지정 시 균등 분배).
    }

    private sealed class VideoExportContext
    {
        public VideoFrameReader? Reader; // Media Foundation(우선). null이면 아래 Player로 폴백.
        public MediaPlayer? Player;
        public Grid Layer = null!;
        public Image Overlay = null!;
        public double NatDurMs;
        public double OutDurMs; // 실효 출력 길이 ms(이 동안 영상 한 바퀴).
        public int W;
        public int H;
    }

    // N = round(duration*fps) 프레임을, 각 움직이는 요소를 시간 t의 상태로 맞춰 렌더해 애니 WebP로 인코딩한다.
    // onFrame(완료프레임, 총프레임): 진행률 보고 + 취소 신호(false 반환 시 중단). 취소되면 -1을 돌려준다.
    private int RenderCurrentPageAnimation(string outputPath, (double Scale, double Fps, double Duration, bool Lossless, int Quality) o, Func<int, int, bool>? onFrame = null)
    {
        const int maxFrames = 300;
        var fps = Math.Clamp(o.Fps, 1, 60);
        var duration = Math.Clamp(o.Duration, 0.1, 60);
        var scale = o.Scale;
        var requested = Math.Max(1, (int)Math.Round(duration * fps));
        var n = Math.Min(maxFrames, requested);
        var delayCs = Math.Max(1, (int)Math.Round(100.0 / fps));

        // 움직이는 요소 수집(애니: 전 프레임 미리 디코드 / 동영상: 플레이어+오버레이 준비).
        var anims = new List<AnimExportContext>();
        var videos = new List<VideoExportContext>();
        foreach (var panel in _panels)
        {
            foreach (var image in panel.Images)
            {
                if (image.Kind == MediaKind.Animated && image.Player != null && image.Image != null)
                {
                    image.FrameTimer?.Stop(); // 라이브 타이머가 Source를 덮어쓰지 않게.
                    var player = image.Player;
                    var fc = Math.Max(1, player.FrameCount);
                    var ctx = new AnimExportContext { Target = image.Image, Frames = new BitmapSource[fc], Delays = new int[fc] };
                    for (var i = 0; i < fc; i++)
                    {
                        ctx.Frames[i] = player.DecodeFrame(i); // 순차 디코드(델타 프레임 정확). 1회만.
                        ctx.Delays[i] = player.DelayMs(i);
                        ctx.Total += ctx.Delays[i];
                    }
                    ctx.OutDurMs = EffectiveOutput(image).Duration * 1000.0;
                    ctx.Override = image.OutputDuration > 0; // 지정 시 균등 분배.
                    anims.Add(ctx);
                }
                else if (image.Kind == MediaKind.Video)
                {
                    videos.Add(SetupVideoExport(image));
                }
            }
        }

        var hasMotion = anims.Count > 0 || videos.Exists(v => (v.Reader != null || v.Player != null) && v.NatDurMs > 0);
        var frameCount = hasMotion ? n : 1; // 움직이는 요소가 없으면 정지 1프레임.

        using var collection = new MagickImageCollection();
        try
        {
            for (var f = 0; f < frameCount; f++)
            {
                if (onFrame != null && !onFrame(f, frameCount))
                {
                    return -1; // 사용자 취소(파일 미기록, finally가 플레이어 정리).
                }

                var tMs = f / fps * 1000.0;

                foreach (var a in anims)
                {
                    a.Target.Source = a.Frames[FrameIndexAtTime(a, tMs)];
                }

                foreach (var v in videos)
                {
                    // 출력 길이(OutDurMs) 동안 영상이 한 바퀴 돌도록 소스 위치를 리타이밍한다.
                    var pos = v.NatDurMs > 0 && v.OutDurMs > 0 ? tMs % v.OutDurMs / v.OutDurMs * v.NatDurMs : tMs;

                    if (v.Reader != null)
                    {
                        // Media Foundation: 그 시각 프레임을 직접 디코드(펌프 없음, 프레임 정확).
                        var bmp = v.Reader.GetFrame(pos);
                        if (bmp != null)
                        {
                            v.Overlay.Source = bmp;
                        }
                        continue;
                    }

                    if (v.Player == null)
                    {
                        continue; // 폴백 정지본 유지.
                    }

                    // 폴백: MediaPlayer 탐색 + 고정 펌프.
                    v.Player.Position = TimeSpan.FromMilliseconds(pos);
                    v.Player.Play();
                    v.Player.Pause();
                    PumpUntil(() => false, 200); // 탐색·디코드 시간. 검은 프레임 나오면 ↑.

                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawVideo(v.Player, new Rect(0, 0, v.W, v.H));
                    }
                    var vrtb = new RenderTargetBitmap(v.W, v.H, 96, 96, PixelFormats.Pbgra32);
                    vrtb.Render(visual);
                    vrtb.Freeze();
                    v.Overlay.Source = vrtb;
                }

                PageSurface.UpdateLayout();
                var frame = RenderPageToBitmap(scale, scale);
                var mi = FrameToMagickImage(frame);
                mi.AnimationDelay = (uint)delayCs;
                mi.AnimationTicksPerSecond = 100;
                collection.Add(mi);
            }

            if (collection.Count == 0)
            {
                return 0; // 방어: 프레임이 하나도 없으면 기록하지 않는다.
            }

            collection[0].AnimationIterations = 0; // 무한 반복.
            collection.Coalesce();
            foreach (var img in collection)
            {
                if (o.Lossless)
                {
                    img.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
                }
                else
                {
                    img.Quality = (uint)Math.Clamp(o.Quality, 1, 100);
                }
            }
            collection.Write(outputPath, MagickFormat.WebP);
            return collection.Count;
        }
        finally
        {
            // 동영상 자원 정리(오버레이·라이브 패널은 호출부의 LoadPage 리로드가 정리한다).
            foreach (var v in videos)
            {
                try { v.Reader?.Dispose(); } catch { /* 무시 */ }
                try { v.Player?.Close(); } catch { /* 무시 */ }
            }
        }
    }

    // 동영상 MediaElement의 NaturalDuration(출력 길이 계산에 필요)이 준비될 때까지 짧게 대기한다.
    // 페이지를 막 로드한 직후엔 비동기 로딩이 끝나지 않아 길이가 0으로 잡힐 수 있다.
    private void WaitForVideosReady()
    {
        bool AllReady()
        {
            foreach (var panel in _panels)
            {
                foreach (var image in panel.Images)
                {
                    if (image.Kind == MediaKind.Video && image.Media != null && !image.Media.NaturalDuration.HasTimeSpan)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        if (AllReady())
        {
            return;
        }

        _pumpingMedia = true;
        try { PumpUntil(AllReady, 3000); }
        finally { _pumpingMedia = false; }
    }

    // 페이지의 움직이는 요소에서 출력 길이(초)·FPS를 도출한다 — 각 이미지의 '출력' 설정(지정 또는 원본) 기준.
    // 길이 = 가장 긴 요소(한 바퀴 다 재생) / FPS = 가장 높은 요소. 움직이는 요소가 없으면 (2초, 12fps).
    private (double Duration, double Fps) ComputeAnimationDefaults()
    {
        var durations = new List<double>();
        var fpsList = new List<double>();

        foreach (var panel in _panels)
        {
            foreach (var image in panel.Images)
            {
                var moving = (image.Kind == MediaKind.Animated && image.Player != null && image.Player.FrameCount > 1)
                             || image.Kind == MediaKind.Video;
                if (!moving)
                {
                    continue;
                }

                var (d, f) = EffectiveOutput(image);
                durations.Add(d);
                fpsList.Add(f);
            }
        }

        var duration = durations.Count > 0 ? Math.Clamp(Math.Round(durations.Max(), 2), 0.1, 60) : 2.0;
        var fps = fpsList.Count > 0 ? Math.Clamp(Math.Round(fpsList.Max()), 1, 60) : 12.0;
        return (duration, fps);
    }

    // 시간 t(ms)에 해당하는 애니 프레임 인덱스. 출력 길이를 지정하면 균등 분배, 아니면 원본 프레임별 지연대로(원본 길이로 루프).
    private static int FrameIndexAtTime(AnimExportContext a, double tMs)
    {
        if (a.Frames.Length <= 1)
        {
            return 0;
        }

        if (a.Override && a.OutDurMs > 0)
        {
            var tau = tMs % a.OutDurMs;
            return Math.Clamp((int)(tau / a.OutDurMs * a.Frames.Length), 0, a.Frames.Length - 1);
        }

        if (a.Total <= 0)
        {
            return 0;
        }

        var m = tMs % a.Total;
        double acc = 0;
        for (var i = 0; i < a.Delays.Length; i++)
        {
            acc += a.Delays[i];
            if (m < acc)
            {
                return i;
            }
        }
        return a.Frames.Length - 1;
    }

    // 동영상 한 개의 내보내기 컨텍스트 준비: 레이어에 오버레이 삽입 + 탐색용 MediaPlayer 열기(실패 시 정지본 폴백).
    private VideoExportContext SetupVideoExport(PanelImage image)
    {
        var path = ResolveProjectPath(image.Path);
        var overlay = new Image
        {
            Stretch = Stretch.Uniform,
            Width = image.Content.Width,
            Height = image.Content.Height,
            RenderTransform = image.Content.RenderTransform, // 동영상과 같은 변환(확대/이동) 공유.
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(overlay, BitmapScalingMode.HighQuality);
        var insertIndex = Math.Max(0, image.Layer.Children.Count - 1); // 선택 테두리 아래, 동영상 위.
        image.Layer.Children.Insert(insertIndex, overlay);

        var ctx = new VideoExportContext { Layer = image.Layer, Overlay = overlay };
        var natDurMs = image.Media != null && image.Media.NaturalDuration.HasTimeSpan
            ? image.Media.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
        ctx.OutDurMs = EffectiveOutput(image).Duration * 1000.0; // 출력 길이대로 리타이밍.

        // 1순위: Media Foundation SourceReader(프레임 정확·빠름, 펌프 불필요).
        var reader = VideoFrameReader.TryCreate(path);
        if (reader != null)
        {
            ctx.Reader = reader;
            ctx.W = reader.Width;
            ctx.H = reader.Height;
            ctx.NatDurMs = natDurMs; // 길이는 라이브 MediaElement에서(이미 준비됨).
            return ctx;
        }

        // 2순위(폴백): MediaPlayer 탐색 + 고정 펌프.
        try
        {
            var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            var opened = false;
            var failed = false;
            player.MediaOpened += (_, _) => opened = true;
            player.MediaFailed += (_, _) => failed = true;
            player.Open(new Uri(path, UriKind.Absolute));
            if (PumpUntil(() => opened || failed, 3000) && !failed
                && player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
            {
                ctx.Player = player;
                ctx.W = player.NaturalVideoWidth;
                ctx.H = player.NaturalVideoHeight;
                ctx.NatDurMs = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            }
            else
            {
                try { player.Close(); } catch { /* 무시 */ }
            }
        }
        catch
        {
            ctx.Player = null;
        }

        if (ctx.Player == null)
        {
            // 탐색 불가: 첫 프레임 정지본으로 채운다(움직이진 않지만 비지 않게).
            var still = CaptureVideoFirstFrame(path) ?? GetVideoStillFrame(path);
            if (still != null)
            {
                overlay.Source = still;
            }
        }

        return ctx;
    }

    // WPF 비트맵 → MagickImage. PNG 바이트 경유라 BGRA/프리멀티플라이 알파가 안전하게 변환된다.
    private static MagickImage FrameToMagickImage(BitmapSource frame)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return new MagickImage(ms);
    }

    // 통합 내보내기 설정 창. 형식(PNG/WebP)·배수, WebP면 무손실·품질을 받아 ExportSettings로 돌려준다.
    // 취소하면 null. 길이·FPS는 각 이미지의 '출력' 설정대로 자동 적용되므로 여기엔 없다.
    // 현재 페이지에 움직이는 요소가 있으면 WebP를 기본 선택한다.
    private ExportSettings? ShowExportDialog(bool allowScript)
    {
        ExportSettings? result = null;

        var dialog = new Window
        {
            Title = "내보내기",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        var dark = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
        var gray = new SolidColorBrush(Color.FromRgb(0x77, 0x72, 0x68));
        var root = new StackPanel { Margin = new Thickness(16) };

        // 형식(PNG 정지 / WebP 움직임).
        root.Children.Add(new TextBlock { Text = "형식", FontWeight = FontWeights.Bold, Foreground = dark, Margin = new Thickness(0, 0, 0, 6) });
        var pngRadio = new RadioButton { Content = "PNG (정지 이미지)", GroupName = "fmt", Foreground = dark, Margin = new Thickness(0, 0, 0, 2) };
        var webpRadio = new RadioButton { Content = "WebP (움직이는 이미지)", GroupName = "fmt", Foreground = dark };
        // 형식 기본값: 지난번 선택 기억(없으면 페이지 움직임 유무로 자동).
        var defaultWebp = _exportWebp ?? HasMovingContent();
        pngRadio.IsChecked = !defaultWebp;
        webpRadio.IsChecked = defaultWebp;
        root.Children.Add(pngRadio);
        root.Children.Add(webpRadio);

        // 비주얼 노벨 모드 전체 내보내기: 스크립트(.txt) 형식 추가.
        var scriptRadio = new RadioButton { Content = "스크립트 (.txt — 모든 페이지 텍스트)", GroupName = "fmt", Foreground = dark, Margin = new Thickness(0, 2, 0, 0) };
        if (allowScript)
        {
            root.Children.Add(scriptRadio);
        }

        // 배수.
        var scaleHeader = new TextBlock { Text = "배수", FontWeight = FontWeights.Bold, Foreground = dark, Margin = new Thickness(0, 12, 0, 6) };
        root.Children.Add(scaleHeader);
        var scaleLabel = new TextBlock { Foreground = dark, FontWeight = FontWeights.Bold, MinWidth = 52, TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var scaleSlider = new Slider { Minimum = 1, Maximum = 4, Value = Math.Clamp(_exportScale, 1, 4), TickFrequency = 0.25, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var scaleRow = new DockPanel();
        DockPanel.SetDock(scaleLabel, Dock.Right);
        scaleRow.Children.Add(scaleLabel);
        scaleRow.Children.Add(scaleSlider);
        root.Children.Add(scaleRow);

        // 애니메이션(WebP) 전용: 무손실/품질 + 길이·FPS 안내. 형식이 PNG면 숨긴다.
        var animPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var losslessCheck = new CheckBox { Content = "무손실", IsChecked = _exportLossless, Foreground = dark, VerticalAlignment = VerticalAlignment.Center };
        var qualityBox = new TextBox { Text = _exportQuality.ToString(), Width = 56, MinHeight = 24, VerticalContentAlignment = VerticalAlignment.Center };
        var qualityLabel = new TextBlock { Text = "품질", Foreground = dark, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) };
        var qualRow = new StackPanel { Orientation = Orientation.Horizontal };
        qualRow.Children.Add(losslessCheck);
        qualRow.Children.Add(qualityLabel);
        qualRow.Children.Add(qualityBox);
        animPanel.Children.Add(qualRow);
        animPanel.Children.Add(new TextBlock
        {
            Text = "길이·FPS는 각 이미지의 '출력' 설정대로 적용됩니다(이미지 선택 시 인스펙터에서 지정).",
            Foreground = gray, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(animPanel);

        int ParseI(TextBox b, int def) => int.TryParse(b.Text, out var v) ? v : def;

        void Refresh()
        {
            scaleLabel.Text = $"{scaleSlider.Value:0.##}×";
            var script = scriptRadio.IsChecked == true;
            // 스크립트는 텍스트만 내보내므로 배수·무손실/품질을 숨긴다.
            scaleHeader.Visibility = scaleRow.Visibility = script ? Visibility.Collapsed : Visibility.Visible;
            animPanel.Visibility = (!script && webpRadio.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            qualityLabel.Opacity = losslessCheck.IsChecked == true ? 0.4 : 1.0;
            qualityBox.IsEnabled = losslessCheck.IsChecked != true;
        }

        pngRadio.Checked += (_, _) => Refresh();
        webpRadio.Checked += (_, _) => Refresh();
        scriptRadio.Checked += (_, _) => Refresh();
        scaleSlider.ValueChanged += (_, _) => Refresh();
        losslessCheck.Checked += (_, _) => Refresh();
        losslessCheck.Unchecked += (_, _) => Refresh();
        Refresh();

        var cancelBtn = new Button { Content = "취소", MinWidth = 72 };
        cancelBtn.Click += (_, _) => dialog.Close();
        var okBtn = new Button { Content = "내보내기", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        okBtn.Click += (_, _) =>
        {
            if (scriptRadio.IsChecked == true)
            {
                result = new ExportSettings { Format = ExportFormat.Script };
                dialog.DialogResult = true;
                return;
            }

            var webpSel = webpRadio.IsChecked == true;
            result = new ExportSettings
            {
                Format = webpSel ? ExportFormat.Webp : ExportFormat.Png,
                Scale = scaleSlider.Value,
                Lossless = losslessCheck.IsChecked == true,
                Quality = Math.Clamp(ParseI(qualityBox, 90), 1, 100)
            };
            // 다음 번 기본값으로 기억(세션·세션 간).
            _exportWebp = webpSel;
            _exportScale = result.Scale;
            _exportLossless = result.Lossless;
            _exportQuality = result.Quality;
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
    // 페이지 배경색이 투명(알파 0)이면 그 영역은 자연히 투명으로 남는다(Pbgra32).
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
            // 배경을 직접 그린다(페이지별 색). PageSurface는 PageFrame 테두리(1px)만큼 오프셋이 있어
            // VisualBrush로 쓰면 좌/상에 1px 투명 여백이 생기므로, 그리드 원점(0,0)에 있는 PanelCanvas를 렌더한다.
            // 배경색이 투명(알파 0)이면 칠해도 아무 효과가 없어 그 영역이 투명으로 남는다(= 투명 내보내기).
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
            Pages = CaptureProjectPages(Path.GetDirectoryName(fileName)),
            FlowText = _flow.Clone(),
            VnTemplates = _vnTemplates.Select(t => CopyPageForStorage(t, Path.GetDirectoryName(fileName))).ToList(),
            VnEditingIndex = _editingTemplate != null ? _vnTemplates.IndexOf(_editingTemplate) : -1
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

    // 새로 만들기(Ctrl+N): 초기 페이지 크기·칸 구성·여백·간격을 정하는 대화상자를 띄운 뒤 그 설정으로 시작한다.
    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowNewProjectDialog(out var pageW, out var pageH, out var pattern, out var margin, out var gutter))
        {
            return; // 취소.
        }

        ClearSelection();
        ComicTitleTextBox.Text = string.Empty;
        _projectFilePath = null;
        _projectBaseDirectory = null;
        _vnTemplates.Clear();

        // 선택한 칸 구성 / 여백 / 간격을 입력칸에도 반영(이후 '칸 구성 적용' 등과 일관).
        LayoutPatternTextBox.Text = pattern;
        AutoMarginTextBox.Text = $"{margin:0.##}";
        AutoGutterTextBox.Text = $"{gutter:0.##}";

        // 선택한 크기의 빈 페이지 하나(기본 흰색 배경)로 교체 후 로드하고, 칸 구성을 채운다.
        ReplacePages(new[] { new ComicPageData { Name = "Page 1", PageWidth = pageW, PageHeight = pageH } });
        _currentPageIndex = 0;
        LoadPage(_pages[0]);
        ApplyFlowText(null); // 본문 텍스트 초기화(빈 본문 + 기본 서식).
        CreateLayoutFromPattern(pattern);
        ClearSelection();
        UpdatePageList();

        ResetHistoryBaseline();
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateUndoRedoButtons();
        UpdateStatus("새 프로젝트를 만들었습니다.");
    }

    // 새 프로젝트 시작 설정 대화상자. 확인이면 true와 값들, 취소면 false. 기본값을 미리 채워 둔다.
    private bool ShowNewProjectDialog(out double pageW, out double pageH, out string pattern, out double margin, out double gutter)
    {
        pageW = 832; pageH = 1216; pattern = "1,2,1"; margin = 24; gutter = 14;

        var widthBox = NewProjectNumberBox("832");
        var heightBox = NewProjectNumberBox("1216");
        var marginBox = NewProjectNumberBox("24");
        var gutterBox = NewProjectNumberBox("14");

        // 칸 구성: 숫자 입력 대신 예시 그림 3개 중 선택.
        var presets = new (string Pattern, int[] Rows, string Label)[]
        {
            ("1", new[] { 1 }, "1 (1칸)"),
            ("2,2", new[] { 2, 2 }, "2,2 (4칸)"),
            ("1,2,1", new[] { 1, 2, 1 }, "1,2,1 (4칸)")
        };
        var selectedPattern = "1,2,1";
        var landscape = false;

        // 방향(세로/가로)에 따라 미리보기 비율이 달라지므로 썸네일을 다시 만든다.
        var layoutRow = new StackPanel { Orientation = Orientation.Horizontal };
        void RebuildThumbs()
        {
            layoutRow.Children.Clear();
            foreach (var p in presets)
            {
                var content = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
                content.Children.Add(BuildLayoutThumbnail(p.Rows, landscape));
                content.Children.Add(new TextBlock
                {
                    Text = p.Label,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 0),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x51, 0x4A))
                });
                var rb = new RadioButton
                {
                    GroupName = "np_layout",
                    Content = content,
                    Tag = p.Pattern,
                    Margin = new Thickness(6, 0, 6, 0),
                    IsChecked = p.Pattern == selectedPattern
                };
                rb.Checked += (_, _) => { if (rb.Tag is string t) selectedPattern = t; };
                layoutRow.Children.Add(rb);
            }
        }
        RebuildThumbs();

        // 방향: 세로(기본)/가로. 가로를 고르면 너비·높이를 맞바꾸고 미리보기 비율도 가로로 바꾼다.
        var portraitRb = new RadioButton { Content = "세로", GroupName = "np_orient", IsChecked = true, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var landscapeRb = new RadioButton { Content = "가로", GroupName = "np_orient", VerticalAlignment = VerticalAlignment.Center };
        void SetLandscape(bool land)
        {
            if (land == landscape)
            {
                return;
            }
            landscape = land;
            (widthBox.Text, heightBox.Text) = (heightBox.Text, widthBox.Text); // 너비↔높이 교환
            RebuildThumbs();
        }
        portraitRb.Checked += (_, _) => SetLandscape(false);
        landscapeRb.Checked += (_, _) => SetLandscape(true);
        var orientRow = new StackPanel { Orientation = Orientation.Horizontal };
        orientRow.Children.Add(portraitRb);
        orientRow.Children.Add(landscapeRb);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(NewProjectRow("페이지 크기", RowWithMultiplier(widthBox, heightBox)));
        panel.Children.Add(NewProjectRow("방향", orientRow));
        panel.Children.Add(NewProjectRow("칸 구성", layoutRow));
        panel.Children.Add(NewProjectRow("여백", marginBox));
        panel.Children.Add(NewProjectRow("간격", gutterBox));

        var okBtn = new Button { Content = "만들기", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "새로 만들기",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            Content = panel
        };
        okBtn.Click += (_, _) => dialog.DialogResult = true;

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        // 입력값 파싱(잘못되면 기본값). 페이지 크기는 100~5000으로 제한.
        pageW = Math.Clamp(ParseDoubleOr(widthBox.Text, 832), 100, 5000);
        pageH = Math.Clamp(ParseDoubleOr(heightBox.Text, 1216), 100, 5000);
        pattern = selectedPattern;
        margin = Math.Max(0, ParseDoubleOr(marginBox.Text, 24));
        gutter = Math.Max(0, ParseDoubleOr(gutterBox.Text, 14));
        return true;
    }

    // 칸 구성 예시 미니 그림: 작은 페이지 안에 행별 칸 배치를 사각형으로 그린다. 가로면 비율을 눕힌다.
    private static FrameworkElement BuildLayoutThumbnail(int[] rows, bool landscape)
    {
        var page = new Border
        {
            Width = landscape ? 82 : 58,
            Height = landscape ? 58 : 82,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xB6, 0xAD)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3) // 페이지 여백 시늉
        };

        var grid = new Grid();
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rows.Length; r++)
        {
            var rowCells = new UniformGrid { Rows = 1, Columns = Math.Max(1, rows[r]) };
            for (var c = 0; c < rows[r]; c++)
            {
                rowCells.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x84, 0x79)),
                    Margin = new Thickness(1.5),
                    CornerRadius = new CornerRadius(1)
                });
            }

            Grid.SetRow(rowCells, r);
            grid.Children.Add(rowCells);
        }

        page.Child = grid;
        return page;
    }

    private static TextBox NewProjectNumberBox(string text) => new()
    {
        Text = text,
        Width = 64,
        Padding = new Thickness(6, 2, 6, 2),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    // "라벨 + 컨트롤" 한 줄(라벨 너비 통일).
    private static FrameworkElement NewProjectRow(string label, FrameworkElement content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 72,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x51, 0x4A))
        });
        row.Children.Add(content);
        return row;
    }

    // 너비 × 높이 입력을 'W × H' 형태로 묶는다.
    private static FrameworkElement RowWithMultiplier(TextBox a, TextBox b)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(a);
        row.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(b);
        return row;
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
            _vnTemplates.Clear();
            foreach (var t in project.VnTemplates)
            {
                _vnTemplates.Add(t);
            }
            _currentPageIndex = Math.Clamp(project.CurrentPageIndex, 0, _pages.Count - 1);
            LoadPage(_pages[_currentPageIndex]);
            ApplyFlowText(project.FlowText); // 본문 텍스트·서식 복원(인스펙터 갱신 + 재분할).
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
