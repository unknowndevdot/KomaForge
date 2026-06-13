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
        BitmapSource[]? frames = null;
        int[]? delays = null;

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
        }
        else if (TryDecodeAnimatedFrames(path, out frames, out delays))
        {
            // 움직이는 gif/webp: 프레임 시퀀스를 타이머로 순환.
            kind = MediaKind.Animated;
            image = CreateImageControl(frames[0], panel, transform);
            content = image;
        }
        else
        {
            // 정지 이미지.
            kind = MediaKind.Static;
            image = CreateImageControl(LoadStaticBitmap(path), panel, transform);
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

        var layer = new Grid
        {
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            // 크롭은 칸 사변형 Clip으로 처리한다(ClipToBounds 대신).
            ClipToBounds = false
        };
        layer.Children.Add(content);
        layer.Children.Add(selectionBorder);

        var panelImage = new PanelImage(panel, path, kind, layer, content, image, media, selectionBorder, scale, translate)
        {
            Frames = frames,
            FrameDelays = delays,
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
            media!.Play();
        }

        return panelImage;
    }

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
        if (panelImage.Image == null || panelImage.Frames == null || panelImage.Frames.Length <= 1)
        {
            return;
        }

        var index = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(20, panelImage.FrameDelays![0]))
        };
        timer.Tick += (_, _) =>
        {
            var frames = panelImage.Frames;
            var delays = panelImage.FrameDelays;
            if (frames == null || delays == null)
            {
                return;
            }

            index = (index + 1) % frames.Length;
            panelImage.Image.Source = frames[index];
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, delays[index]));
        };
        panelImage.FrameTimer = timer;
        timer.Start();
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
    private static bool TryDecodeAnimatedFrames(string path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BitmapSource[]? frames,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out int[]? delays)
    {
        frames = null;
        delays = null;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".gif" or ".webp" or ".png" or ".apng"))
        {
            return false;
        }

        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null || codec.FrameCount <= 1)
            {
                return false;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var frameInfos = codec.FrameInfo;
            var outFrames = new BitmapSource[codec.FrameCount];
            var outDelays = new int[codec.FrameCount];

            SKBitmap? previous = null;
            for (var i = 0; i < codec.FrameCount; i++)
            {
                var bitmap = new SKBitmap(info);
                var options = new SKCodecOptions(i);
                if (frameInfos[i].RequiredFrame != -1 && previous != null)
                {
                    // 직전 프레임 픽셀 위에 현재 프레임의 변화분을 합성한다(버퍼엔 i-1 프레임이 들어있다).
                    previous.CopyTo(bitmap);
                    options = new SKCodecOptions(i, i - 1);
                }

                codec.GetPixels(info, bitmap.GetPixels(), options);

                var wpf = BitmapSource.Create(info.Width, info.Height, 96, 96,
                    PixelFormats.Pbgra32, null, bitmap.Bytes, info.RowBytes);
                wpf.Freeze();
                outFrames[i] = wpf;

                var duration = frameInfos[i].Duration;
                outDelays[i] = duration > 0 ? duration : 100;

                previous?.Dispose();
                previous = bitmap;
            }

            previous?.Dispose();
            frames = outFrames;
            delays = outDelays;
            return true;
        }
        catch
        {
            // 코덱 미설치/디코드 실패: 정지 이미지로 폴백.
            return false;
        }
    }

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
