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
    // --- 정지 이미지 디코드 캐시(경로 기준 LRU) ---
    // 페이지를 넘길 때마다 같은 '정지' 이미지를 다시 디코드하지 않도록 결과를 경로별로 보관한다.
    // 총 디코드 바이트가 한도를 넘으면 '가장 오래 안 쓴 것'부터 버린다(시간↔메모리 맞교환).
    // 움직이는 gif/webp는 메모리가 커서 캐시 대상이 아니며, AnimatedPlayer로 재생하며 프레임마다 디코드한다.
    private int _imageCacheLimitMb = 256; // 환경설정-일반에서 조절. 0이면 캐시 끔.
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _imageCache = new();
    private readonly LinkedList<CacheEntry> _imageCacheList = new(); // 앞=최근 사용, 뒤=오래됨.
    private long _imageCacheBytes;
    // 인접 페이지 예열 작업의 세대 번호. 페이지가 바뀌면 증가시켜 진행 중인 예열을 취소한다.
    private volatile int _prefetchGen;
    // 이미 예열 작업을 띄운 세대(같은 세대에 중복 작업이 생기지 않게 한다).
    private int _prefetchStartedGen = -1;

    private sealed class CacheEntry
    {
        public CacheEntry(string key, BitmapSource bitmap, long bytes, System.DateTime lastWriteUtc)
        {
            Key = key;
            Bitmap = bitmap;
            Bytes = bytes;
            LastWriteUtc = lastWriteUtc;
        }

        public string Key;
        public BitmapSource Bitmap;
        public long Bytes;                  // 디코드 바이트(대략 W*H*4).
        public System.DateTime LastWriteUtc; // 원본 파일 수정시각(파일 교체 감지용).
    }

    private long ImageCacheLimitBytes => (long)_imageCacheLimitMb * 1024 * 1024;

    // 경로로 비트맵을 얻는다. 캐시에 최신본이 있으면 만들지 않고 재사용하고, 없으면 create로 만들어 캐시한다.
    // 정지 이미지 디코드와 동영상 첫 프레임(포스터) 모두 이 한 곳을 거쳐 같은 LRU·한도를 공유한다.
    private BitmapSource? GetOrCreateCachedBitmap(string path, Func<BitmapSource?> create)
    {
        System.DateTime writeUtc = default;
        try { writeUtc = File.GetLastWriteTimeUtc(path); } catch { /* 접근 불가: 기본값으로 둔다 */ }
        var key = path.ToLowerInvariant();

        if (_imageCache.TryGetValue(key, out var node))
        {
            if (node.Value.LastWriteUtc == writeUtc)
            {
                _imageCacheList.Remove(node);     // 최근 사용으로 갱신(MRU = 맨 앞).
                _imageCacheList.AddFirst(node);
                return node.Value.Bitmap;
            }

            RemoveImageCacheNode(node); // 파일이 바뀜(stale) → 버리고 새로 만든다.
        }

        var bitmap = create();
        if (bitmap != null)
        {
            AddToImageCache(key, bitmap, FrameBytes(bitmap), writeUtc);
        }

        return bitmap;
    }

    // 정지 이미지: 캐시 미스면 디코드한다(항상 비트맵을 돌려준다).
    private BitmapSource GetOrDecodeStaticBitmap(string path)
        => GetOrCreateCachedBitmap(path, () => LoadStaticBitmap(path))!;

    private static long FrameBytes(BitmapSource bmp) => (long)bmp.PixelWidth * bmp.PixelHeight * 4; // Pbgra32 기준 근사.

    private void AddToImageCache(string key, BitmapSource bitmap, long bytes, System.DateTime lastWriteUtc)
        => InsertCacheNode(key, bitmap, bytes, lastWriteUtc, atFront: true);

    // 캐시에 노드를 삽입한다. atFront=true면 MRU(맨 앞), false면 LRU(맨 뒤=가장 먼저 버려짐, 예열용).
    private void InsertCacheNode(string key, BitmapSource bitmap, long bytes, System.DateTime lastWriteUtc, bool atFront)
    {
        if (ImageCacheLimitBytes <= 0)
        {
            return; // 캐시 꺼짐.
        }

        if (_imageCache.ContainsKey(key))
        {
            return; // 이미 있음(예열과 실제 로드가 겹칠 때 중복 삽입 방지).
        }

        var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, bitmap, bytes, lastWriteUtc));
        if (atFront)
        {
            _imageCacheList.AddFirst(node);
        }
        else
        {
            _imageCacheList.AddLast(node); // 예열 항목은 맨 뒤 → 한도 초과 시 현재 페이지보다 먼저 버려진다.
        }

        _imageCache[key] = node;
        _imageCacheBytes += bytes;
        TrimImageCache();
    }

    // 캐시에서 노드를 제거한다(캐시의 강한 참조만 내려놓음 → 사용 중인 Image는 영향 없음).
    private void RemoveImageCacheNode(LinkedListNode<CacheEntry> node)
    {
        _imageCacheList.Remove(node);
        _imageCache.Remove(node.Value.Key);
        _imageCacheBytes -= node.Value.Bytes;
    }

    // 한도를 넘는 동안 가장 오래 안 쓴 항목부터 제거한다.
    // 단, '가장 최근 항목 하나'는 한도보다 커도 남긴다(큰 단일 이미지가 매번 재디코드되는 것을 막는다).
    private void TrimImageCache()
    {
        var limit = ImageCacheLimitBytes;
        while (_imageCacheBytes > limit && _imageCacheList.Count > 1)
        {
            RemoveImageCacheNode(_imageCacheList.Last!);
        }
    }

    // 환경설정에서 한도를 바꿨을 때 적용(0이면 전부 비움, 그 외엔 새 한도로 정리).
    private void ApplyImageCacheLimit(int limitMb)
    {
        _imageCacheLimitMb = limitMb < 0 ? 0 : limitMb;
        if (_imageCacheLimitMb == 0)
        {
            _imageCache.Clear();
            _imageCacheList.Clear();
            _imageCacheBytes = 0;
            return;
        }

        TrimImageCache();
    }

    // 페이지가 바뀔 때 호출: 진행 중인 예열을 취소하고, 현재 페이지가 안정된 뒤 인접 페이지를 예열한다.
    private void BeginPrefetchAdjacentPages()
    {
        _prefetchGen++; // 이전 페이지의 예열 취소.
        if (_imageCacheLimitMb <= 0 || _exporting)
        {
            return;
        }

        // 전환 자체의 반응성을 해치지 않도록 한 박자 뒤(백그라운드 우선순위)에 시작한다.
        Dispatcher.BeginInvoke(new Action(StartAdjacentPrefetch), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void StartAdjacentPrefetch()
    {
        var gen = _prefetchGen;
        if (_prefetchStartedGen == gen)
        {
            return; // 이 세대 예열은 이미 띄움(빠른 연속 전환 시 중복 작업 방지).
        }

        var targets = CollectPrefetchPaths();
        if (targets.Count == 0)
        {
            return;
        }

        _prefetchStartedGen = gen;
        System.Threading.Tasks.Task.Run(() => PrefetchWorker(gen, targets));
    }

    // 다음 페이지(우선), 이전 페이지의 '정지 이미지' 경로 중 아직 캐시에 없는 것을 모은다(UI 스레드).
    private List<string> CollectPrefetchPaths()
    {
        var result = new List<string>();
        if (_imageCacheLimitMb <= 0 || _exporting)
        {
            return result;
        }

        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var idx in new[] { _currentPageIndex + 1, _currentPageIndex - 1 })
        {
            if (idx < 0 || idx >= _pages.Count)
            {
                continue;
            }

            foreach (var panelData in _pages[idx].Panels)
            {
                foreach (var imageData in panelData.Images)
                {
                    var ext = System.IO.Path.GetExtension(imageData.Path).ToLowerInvariant();
                    if (IsVideoExtension(ext))
                    {
                        continue; // 동영상은 예열 대상 아님(첫 프레임 캡처는 UI 스레드에 묶여 있음).
                    }

                    var resolved = ResolveProjectPath(imageData.Path);
                    var key = resolved.ToLowerInvariant();
                    if (!seen.Add(key) || _imageCache.ContainsKey(key) || !File.Exists(resolved))
                    {
                        continue;
                    }

                    result.Add(resolved);
                }
            }
        }

        return result;
    }

    // 백그라운드: 경로들을 디코드해 UI 스레드로 보내 캐시에 '예열용(evict-first)'으로 넣는다.
    private void PrefetchWorker(int gen, List<string> targets)
    {
        foreach (var path in targets)
        {
            if (gen != _prefetchGen)
            {
                return; // 페이지가 바뀜 → 취소.
            }

            try
            {
                // 애니(gif/webp/apng)는 정지 캐시 대상이 아니므로 건너뛴다.
                var player = AnimatedPlayer.TryCreate(path);
                if (player != null)
                {
                    player.Dispose();
                    continue;
                }

                var writeUtc = File.GetLastWriteTimeUtc(path);
                var bitmap = LoadStaticBitmap(path);
                var bytes = FrameBytes(bitmap);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _prefetchGen)
                    {
                        return;
                    }

                    InsertCacheNode(path.ToLowerInvariant(), bitmap, bytes, writeUtc, atFront: false);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
                // 디코드 실패 항목은 건너뛴다(예열은 best-effort).
            }
        }
    }

    private PanelImage AddPanelImage(ComicPanel panel, string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translate);

        MediaKind kind;
        FrameworkElement content;
        Image? image = null;
        MediaElement? media = null;
        AnimatedPlayer? animatedPlayer = null;
        Image? videoPoster = null; // 동영상 로딩 동안 보여줄 첫 프레임(셸 썸네일).

        if (IsVideoExtension(ext))
        {
            // 동영상: MediaElement(음소거·루프).
            kind = MediaKind.Video;
            media = new MediaElement
            {
                Source = new Uri(path, UriKind.Absolute),
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
                IsMuted = true,
                Width = panel.Frame.Width,
                Height = panel.Frame.Height,
                RenderTransform = transform,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            media.MediaEnded += (_, _) =>
            {
                media.Position = TimeSpan.FromMilliseconds(1);
                media.Play();
            };
            var fileName = System.IO.Path.GetFileName(path);
            media.MediaFailed += (_, _) => UpdateStatus($"동영상을 재생할 수 없습니다: {fileName}");
            content = media;

            // 동영상은 로딩되는 동안 빈(검은) 화면이라, 첫 프레임을 뒤에 깔아 둔다.
            // 영상이 실제로 뜨면 그 위를 덮으므로, 페이지 진입 시 빈 공간 대신 첫 프레임으로 시작한다.
            // 내보내기와 동일하게 풀해상도 MediaPlayer 캡처를 우선 쓰고, 실패하면 셸 썸네일로 폴백한다(결과는 캐시).
            var poster = GetOrCreateCachedBitmap(path, () => CaptureVideoFirstFrame(path) ?? GetVideoStillFrame(path));
            if (poster != null)
            {
                videoPoster = new Image
                {
                    Source = poster,
                    Stretch = Stretch.Uniform,
                    Width = panel.Frame.Width,
                    Height = panel.Frame.Height,
                    RenderTransform = transform,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(videoPoster, BitmapScalingMode.HighQuality);
            }
        }
        else if ((animatedPlayer = AnimatedPlayer.TryCreate(path)) != null)
        {
            // 움직이는 gif/webp/apng: 재생하며 프레임마다 디코드(메모리·첫 진입 멈춤 최소화).
            // 첫 프레임(0)을 디코드해 바로 표시하고, 나머지는 타이머가 그때그때 디코드한다.
            kind = MediaKind.Animated;
            image = CreateImageControl(animatedPlayer.DecodeFrame(0), panel, transform);
            content = image;
        }
        else
        {
            // 정지 이미지: 디코드 캐시를 거친다(같은 파일이면 재디코드 생략 → 페이지 전환 가속).
            kind = MediaKind.Static;
            image = CreateImageControl(GetOrDecodeStaticBitmap(path), panel, transform);
            content = image;
        }

        // 선택 표시는 테두리 대신 살짝 강조색 틴트 오버레이로 한다.
        // 이미지와 같은 변환을 공유하고(함께 이동/확대), 비트맵 알파를 OpacityMask로 써서 이미지 모양에만 입힌다.
        var selectionBorder = new Border
        {
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            Background = new SolidColorBrush(Color.FromArgb(70, 43, 111, 106)),
            BorderThickness = new Thickness(0),
            IsHitTestVisible = false,
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Hidden
        };
        if (image?.Source is BitmapSource maskBitmap)
        {
            // 정지/애니 이미지: 보이는 픽셀(레터박스·투명 제외)에만 틴트가 입혀지도록 마스크.
            selectionBorder.OpacityMask = new ImageBrush(maskBitmap) { Stretch = Stretch.Uniform };
        }

        // 색 그라데이션 오버레이: 이미지와 같은 변환을 공유하고, 이미지 모양에만 입히도록 같은 마스크를 쓴다.
        var gradientOverlay = new Border
        {
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            IsHitTestVisible = false,
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        if (image?.Source is BitmapSource gMask)
        {
            gradientOverlay.OpacityMask = new ImageBrush(gMask) { Stretch = Stretch.Uniform };
        }

        var layer = new Grid
        {
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            // 크롭은 칸 사변형 Clip으로 처리한다(ClipToBounds 대신).
            ClipToBounds = false
        };
        if (videoPoster != null)
        {
            layer.Children.Add(videoPoster); // 동영상(content) 아래 → 영상이 뜨면 가려진다.
        }
        layer.Children.Add(content);
        layer.Children.Add(gradientOverlay); // 콘텐츠 위, 선택 틴트 아래.
        layer.Children.Add(selectionBorder);

        var panelImage = new PanelImage(panel, path, kind, layer, content, image, media, selectionBorder, scale, translate)
        {
            Player = animatedPlayer,
            GradientOverlay = gradientOverlay,
            Id = NewObjectId()
        };
        panel.Images.Add(panelImage);
        panel.ImageCanvas.Children.Add(layer);
        panel.SelectedImage = panelImage;
        _selectedImage = panelImage;
        UpdatePanelImageSizes(panel);
        UpdateImageOrder(panel);
        UpdateImageList(panel);
        panel.Placeholder.Visibility = Visibility.Collapsed;

        if (kind == MediaKind.Animated)
        {
            StartFrameAnimation(panelImage);
        }
        else if (kind == MediaKind.Video)
        {
            // 길이가 로드되면 출력 길이 지정에 맞춰 재생 속도를 적용한다.
            media!.MediaOpened += (_, _) => ApplyVideoSpeed(panelImage);
            media.Play();
        }

        return panelImage;
    }

    // 가장자리 그라데이션 적용. 대상 색이 투명(알파 0)이면 이미지를 점점 사라지게(OpacityMask),
    // 색이면 그 색을 방향 변에서 반대편으로 페이드시켜 칠한다(오버레이). 게이지로 페이드 구간을 정한다.
    private static void ApplyImageGradient(PanelImage image)
    {
        var overlay = image.GradientOverlay;
        if (overlay == null)
        {
            return;
        }

        if (image.GradientDirection == ImageGradientDirection.None)
        {
            overlay.Background = null;
            image.Content.OpacityMask = null;
            return;
        }

        // 축: u=0이 대상(방향) 변, u=1이 원본(반대편) 변(RelativeToBoundingBox).
        var (start, end) = image.GradientDirection switch
        {
            ImageGradientDirection.Top => (new Point(0, 0), new Point(0, 1)),
            ImageGradientDirection.Bottom => (new Point(0, 1), new Point(0, 0)),
            ImageGradientDirection.Left => (new Point(0, 0), new Point(1, 0)),
            _ => (new Point(1, 0), new Point(0, 0)), // Right
        };
        var s = Math.Clamp(Math.Min(image.GradientStart, image.GradientEnd), 0, 100) / 100.0;
        var e = Math.Clamp(Math.Max(image.GradientStart, image.GradientEnd), 0, 100) / 100.0;

        if (image.GradientColor.A == 0)
        {
            // 투명 모드: 이미지 자체를 대상 변에서 사라지게 한다(색 오버레이 없음).
            overlay.Background = null;
            var clear = Color.FromArgb(0, 255, 255, 255);
            var solid = Color.FromArgb(255, 255, 255, 255);
            var mask = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            mask.GradientStops.Add(new GradientStop(clear, 0));   // 대상 변 = 완전 투명(사라짐)
            mask.GradientStops.Add(new GradientStop(clear, s));
            mask.GradientStops.Add(new GradientStop(solid, e));   // 이후 = 원본(불투명)
            mask.GradientStops.Add(new GradientStop(solid, 1));
            mask.Freeze();
            image.Content.OpacityMask = mask;
            return;
        }

        // 색 모드: 대상 변에서 그 색(불투명) → 반대편으로 갈수록 색이 사라져 원본이 드러난다.
        image.Content.OpacityMask = null;
        var c = image.GradientColor;
        var cClear = Color.FromArgb(0, c.R, c.G, c.B);
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(c, 0));        // 대상 변 = 색
        brush.GradientStops.Add(new GradientStop(c, s));
        brush.GradientStops.Add(new GradientStop(cClear, e));   // 이후 = 색 없음(원본)
        brush.GradientStops.Add(new GradientStop(cClear, 1));
        brush.Freeze();
        overlay.Background = brush;
    }

    // 콤보 Tag 문자열 ↔ 방향 enum.
    private static ImageGradientDirection ParseGradientDirection(string? tag) => tag switch
    {
        "Top" => ImageGradientDirection.Top,
        "Bottom" => ImageGradientDirection.Bottom,
        "Left" => ImageGradientDirection.Left,
        "Right" => ImageGradientDirection.Right,
        _ => ImageGradientDirection.None
    };

    // 이미지의 중심을 콘텐츠 좌표상의 지정 지점에 맞춘다(스케일과 무관, 중심 기준 변환).
    private static void CenterImageAtPoint(PanelImage image, Point center)
    {
        image.Translate.X = center.X - image.Content.Width / 2.0;
        image.Translate.Y = center.Y - image.Content.Height / 2.0;
    }

    // 원본(100%) 픽셀 크기로 보이도록 스케일을 맞춘다(콘텐츠는 Uniform으로 칸에 맞춰져 있으므로 그 역수).
    private static void ApplyNativeScale(PanelImage image)
    {
        var contentW = image.Content.Width;
        var contentH = image.Content.Height;

        double nativeW = 0, nativeH = 0;
        if (image.Image?.Source is BitmapSource bitmap)
        {
            nativeW = bitmap.PixelWidth;
            nativeH = bitmap.PixelHeight;
        }
        else if (image.Media != null && image.Media.NaturalVideoWidth > 0)
        {
            nativeW = image.Media.NaturalVideoWidth;
            nativeH = image.Media.NaturalVideoHeight;
        }

        if (nativeW <= 0 || nativeH <= 0 || contentW <= 0 || contentH <= 0)
        {
            return;
        }

        var uniform = Math.Min(contentW / nativeW, contentH / nativeH); // 칸에 맞춘 배율
        if (uniform <= 0)
        {
            return;
        }

        var scale = 1.0 / uniform; // 그 역수 = 원본 100%
        image.Scale.ScaleX = scale;
        image.Scale.ScaleY = scale;
    }

    // 이미지가 칸에 맞춰진(Uniform) 콘텐츠 대비 원본 100%가 되는 배율. 알 수 없으면 0.
    private static double NativeScaleOf(PanelImage image)
    {
        var contentW = image.Content.Width;
        var contentH = image.Content.Height;

        double nativeW = 0, nativeH = 0;
        if (image.Image?.Source is BitmapSource bitmap)
        {
            nativeW = bitmap.PixelWidth;
            nativeH = bitmap.PixelHeight;
        }
        else if (image.Media != null && image.Media.NaturalVideoWidth > 0)
        {
            nativeW = image.Media.NaturalVideoWidth;
            nativeH = image.Media.NaturalVideoHeight;
        }

        if (nativeW <= 0 || nativeH <= 0 || contentW <= 0 || contentH <= 0)
        {
            return 0;
        }
        var uniform = Math.Min(contentW / nativeW, contentH / nativeH);
        return uniform > 0 ? 1.0 / uniform : 0;
    }

    // 휠 확대 상한: 원본 픽셀의 4배(= 4 × 원본100%배율). 원본이 작은 이미지는 기존 상한(5.0) 유지.
    private static double MaxImageZoomScale(PanelImage image)
    {
        var native = NativeScaleOf(image);
        return native > 0 ? Math.Max(5.0, 4.0 * native) : 5.0;
    }

    private static Image CreateImageControl(BitmapSource source, ComicPanel panel, Transform transform)
    {
        var image = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private static void StartFrameAnimation(PanelImage panelImage)
    {
        var player = panelImage.Player;
        if (panelImage.Image == null || player == null || player.FrameCount <= 1)
        {
            return;
        }

        var fc = player.FrameCount;
        // 출력 길이를 지정하면 전 프레임을 그 시간 동안 균등 재생, 미지정(0)이면 원본 프레임별 지연대로.
        var overrideMs = panelImage.OutputDuration > 0 ? Math.Max(20, panelImage.OutputDuration * 1000.0 / fc) : 0;

        var index = 0; // 프레임 0은 AddPanelImage에서 이미 디코드해 표시했고 플레이어 버퍼에 들어있다.
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(overrideMs > 0 ? overrideMs : Math.Max(20, player.DelayMs(0)))
        };
        timer.Tick += (_, _) =>
        {
            var p = panelImage.Player;
            if (p == null)
            {
                return;
            }

            index = (index + 1) % p.FrameCount;
            try
            {
                // 다음 프레임을 그때그때 디코드한다(전 프레임을 메모리에 들고 있지 않음).
                panelImage.Image.Source = p.DecodeFrame(index);
            }
            catch
            {
                return; // 디코드 실패 프레임은 건너뛴다(다음 틱에서 회복 시도).
            }

            timer.Interval = TimeSpan.FromMilliseconds(overrideMs > 0 ? overrideMs : Math.Max(20, p.DelayMs(index)));
        };
        panelImage.FrameTimer = timer;
        timer.Start();
    }

    // 이미지의 실제 출력값 (길이초, fps). 지정값이 있으면 그것, 없으면 원본에서 산출.
    private static (double Duration, double Fps) EffectiveOutput(PanelImage image)
    {
        if (image.Kind == MediaKind.Animated && image.Player != null && image.Player.FrameCount > 1)
        {
            var p = image.Player;
            double totalMs = 0;
            for (var i = 0; i < p.FrameCount; i++)
            {
                totalMs += p.DelayMs(i);
            }
            var srcDur = totalMs > 0 ? totalMs / 1000.0 : 1.0;
            var srcFps = srcDur > 0 ? p.FrameCount / srcDur : 12.0;
            return (image.OutputDuration > 0 ? image.OutputDuration : srcDur,
                    image.OutputFps > 0 ? image.OutputFps : srcFps);
        }

        if (image.Kind == MediaKind.Video)
        {
            var natSec = image.Media != null && image.Media.NaturalDuration.HasTimeSpan
                ? image.Media.NaturalDuration.TimeSpan.TotalSeconds : 0;
            var srcDur = natSec > 0 ? natSec : 1.0;
            return (image.OutputDuration > 0 ? image.OutputDuration : srcDur,
                    image.OutputFps > 0 ? image.OutputFps : 30.0);
        }

        return (image.OutputDuration > 0 ? image.OutputDuration : 1.0, image.OutputFps > 0 ? image.OutputFps : 12.0);
    }

    // 출력 설정대로 라이브 재생을 갱신한다(애니: 타이머 재시작 / 동영상: 재생 속도).
    private void ApplyImageOutputTiming(PanelImage image)
    {
        if (image.Kind == MediaKind.Animated && image.Player != null)
        {
            image.FrameTimer?.Stop();
            StartFrameAnimation(image);
        }
        else if (image.Kind == MediaKind.Video)
        {
            ApplyVideoSpeed(image);
        }
    }

    // 동영상 재생 속도 = 원본길이 / 지정길이(지정 없으면 1배). NaturalDuration이 준비된 뒤에만 의미가 있다.
    private static void ApplyVideoSpeed(PanelImage image)
    {
        var media = image.Media;
        if (media == null)
        {
            return;
        }

        var natSec = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : 0;
        media.SpeedRatio = image.OutputDuration > 0 && natSec > 0 ? natSec / image.OutputDuration : 1.0;
    }

    private static bool IsVideoExtension(string ext) =>
        ext is ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv" or ".m4v";

    // 정지 이미지 로드: WPF 기본 디코더로 시도하고, 실패하면(예: OS 코덱 없는 webp) SkiaSharp로 폴백한다.
    private static BitmapSource LoadStaticBitmap(string path)
    {
        try
        {
            return LoadBitmap(path);
        }
        catch
        {
            var skia = SkiaDecodeSingle(path);
            if (skia != null)
            {
                return skia;
            }

            throw;
        }
    }

    private static BitmapSource? SkiaDecodeSingle(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null)
            {
                return null;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            codec.GetPixels(info, bitmap.GetPixels());
            var wpf = BitmapSource.Create(info.Width, info.Height, 96, 96,
                PixelFormats.Pbgra32, null, bitmap.Bytes, info.RowBytes);
            wpf.Freeze();
            return wpf;
        }
        catch
        {
            return null;
        }
    }

    // gif/webp 등에서 움직이는(2프레임 이상) 프레임을 SkiaSharp로 디코드한다. 정지/실패면 false.
    private void RemovePanelImage(PanelImage image)
    {
        var panel = image.OwnerPanel;
        image.StopPlayback();
        // 레이어가 실제로 들어있는 캔버스(크롭 ON=ImageCanvas, OFF=FreeImageCanvas)에서 제거한다.
        if (image.Layer.Parent is Canvas parentCanvas)
        {
            parentCanvas.Children.Remove(image.Layer);
        }

        panel.Images.Remove(image);

        if (_selectedImage == image)
        {
            _selectedImage = panel.Images.LastOrDefault();
        }

        panel.SelectedImage = _selectedImage?.OwnerPanel == panel ? _selectedImage : panel.Images.LastOrDefault();
        _selectedImage = panel.SelectedImage;
        panel.Placeholder.Visibility = Visibility.Collapsed;
        UpdateImageOrder(panel);
        UpdateImageList(panel);
        UpdateSelectionVisuals();
    }

    private bool DeleteSelectedImage()
    {
        if (_selectedImage == null)
        {
            return false;
        }

        var panel = _selectedImage.OwnerPanel;
        var panelNumber = panel.Number;
        var index = panel.Images.IndexOf(_selectedImage);
        RemovePanelImage(_selectedImage);

        // 삭제 후 선택: 위(index-1) → 없으면 아래(0) → 하나도 없으면 미선택.
        var next = NeighborAfterDelete(panel.Images, index);
        if (next != null)
        {
            SelectImage(next);
            ScrollInspectorToSection();
        }
        else
        {
            ClearSelection(announce: false);
        }

        UpdateStatus($"{panelNumber}번 칸의 선택 이미지를 제거했습니다.");
        return true;
    }

    private bool DeleteSelectedBubble()
    {
        if (_selectedBubble == null)
        {
            return false;
        }

        var panel = _selectedBubble.OwnerPanel;
        var index = panel.Bubbles.IndexOf(_selectedBubble);
        RemoveBubbleFromCurrentParent(_selectedBubble);
        panel.Bubbles.Remove(_selectedBubble);
        _selectedBubble = null;
        UpdateMergedBubbleOutlines();
        UpdateBubbleList(panel);

        // 삭제 후 선택: 위(index-1) → 없으면 아래(0) → 하나도 없으면 미선택.
        var next = NeighborAfterDelete(panel.Bubbles, index);
        if (next != null)
        {
            SelectBubble(next);
            ScrollInspectorToSection();
        }
        else
        {
            ClearSelection(announce: false);
        }

        RefreshCurrentPageLabel(); // 비주얼 노벨 모드 페이지 목록 요약 갱신.
        UpdateStatus("말풍선을 삭제했습니다.");
        return true;
    }

    // 삭제된 항목(index 위치) 기준으로 선택할 이웃: 위(index-1) → 없으면 첫 항목(아래) → 비었으면 null.
    private static T? NeighborAfterDelete<T>(IReadOnlyList<T> list, int index) where T : class
    {
        if (index - 1 >= 0 && index - 1 < list.Count)
        {
            return list[index - 1];
        }

        return list.Count > 0 ? list[0] : null;
    }

    private void MoveSelectedImage(int direction)
    {
        if (_selectedImage == null || _selectedPanel == null || _selectedImage.OwnerPanel != _selectedPanel)
        {
            UpdateStatus("순서를 바꿀 이미지를 먼저 선택하세요.");
            return;
        }

        var images = _selectedPanel.Images;
        var index = images.IndexOf(_selectedImage);
        var nextIndex = index + direction;

        if (index < 0 || nextIndex < 0 || nextIndex >= images.Count)
        {
            return;
        }

        images.RemoveAt(index);
        images.Insert(nextIndex, _selectedImage);
        UpdateImageOrder(_selectedPanel);
        UpdateImageList(_selectedPanel);
        ImageListBox.SelectedItem = _selectedImage;
        UpdateStatus("이미지 순서를 변경했습니다.");
    }

    private void MoveSelectedBubble(int direction)
    {
        if (_selectedBubble == null || _selectedPanel == null || _selectedBubble.OwnerPanel != _selectedPanel)
        {
            UpdateStatus("순서를 바꿀 말풍선을 먼저 선택하세요.");
            return;
        }

        var bubbles = _selectedPanel.Bubbles;
        var index = bubbles.IndexOf(_selectedBubble);
        var nextIndex = index + direction;

        if (index < 0 || nextIndex < 0 || nextIndex >= bubbles.Count)
        {
            return;
        }

        bubbles.RemoveAt(index);
        bubbles.Insert(nextIndex, _selectedBubble);
        UpdateBubbleOrder(_selectedPanel);
        UpdateBubbleList(_selectedPanel);
        BubbleListBox.SelectedItem = _selectedBubble;
        RefreshCurrentPageLabel(); // 비주얼 노벨 모드 페이지 목록 요약(말풍선 순서) 갱신.
        UpdateStatus("말풍선 순서를 변경했습니다.");
    }

    private void MoveSelectedPanel(int direction)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("순서를 바꿀 칸을 먼저 선택하세요.");
            return;
        }

        var index = _panels.IndexOf(_selectedPanel);
        var nextIndex = index + direction;

        if (index < 0 || nextIndex < 0 || nextIndex >= _panels.Count)
        {
            return;
        }

        _panels.RemoveAt(index);
        _panels.Insert(nextIndex, _selectedPanel);
        UpdatePanelOrder();
        UpdatePanelList();
        PanelListBox.SelectedItem = _selectedPanel;
        RefreshCurrentPageLabel(); // 칸 순서가 바뀌면 말풍선 요약 순서도 바뀌므로 페이지 목록 갱신.
        UpdateStatus("칸 순서를 변경했습니다.");
    }

    private void UpdatePanelOrder()
    {
        for (var index = 0; index < _panels.Count; index++)
        {
            Panel.SetZIndex(_panels[index].Frame, index);
        }
    }

    private void UpdatePanelList()
    {
        // 칸 리스트 강조는 '칸'이 활성 선택일 때만(이미지/말풍선 선택 시엔 맥락 칸이어도 강조 안 함).
        var selectedPanel = _selectionKind == SelectionKind.Panel ? _selectedPanel : null;
        _isUpdatingPanelList = true;
        PanelListBox.Items.Clear();

        foreach (var panel in _panels)
        {
            PanelListBox.Items.Add(panel);
        }

        PanelListBox.SelectedItem = selectedPanel;
        _isUpdatingPanelList = false;
    }

    private static void UpdateImageOrder(ComicPanel panel)
    {
        for (var index = 0; index < panel.Images.Count; index++)
        {
            Panel.SetZIndex(panel.Images[index].Layer, index);
        }
    }

    private static void UpdateBubbleOrder(ComicPanel panel)
    {
        // 각 말풍선의 채움/외곽선(ShapePath)은 자기 글자(Container) 바로 아래,
        // 다음(상위) 말풍선보다는 아래에 오도록 짝수/홀수 z로 배치한다.
        for (var index = 0; index < panel.Bubbles.Count; index++)
        {
            Panel.SetZIndex(panel.Bubbles[index].ShapePath, index * 2);
            Panel.SetZIndex(panel.Bubbles[index].Container, index * 2 + 1);
        }
    }

}
