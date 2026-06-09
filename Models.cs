using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KomaForge;

public sealed class ComicPanel
{
    public ComicPanel(
        int number,
        Border frame,
        Canvas imageCanvas,
        TextBlock placeholder,
        Canvas overlay,
        System.Windows.Shapes.Path bubbleFillPath,
        System.Windows.Shapes.Path bubbleOutlinePath,
        Canvas freeOverlay,
        System.Windows.Shapes.Path freeBubbleFillPath,
        System.Windows.Shapes.Path freeBubbleOutlinePath,
        Thumb resizeHandle)
    {
        Number = number;
        Frame = frame;
        ImageCanvas = imageCanvas;
        Placeholder = placeholder;
        Overlay = overlay;
        BubbleFillPath = bubbleFillPath;
        BubbleOutlinePath = bubbleOutlinePath;
        FreeOverlay = freeOverlay;
        FreeBubbleFillPath = freeBubbleFillPath;
        FreeBubbleOutlinePath = freeBubbleOutlinePath;
        ResizeHandle = resizeHandle;
    }

    public int Number { get; set; }
    public Border Frame { get; }
    public Canvas ImageCanvas { get; }
    public TextBlock Placeholder { get; }
    public Canvas Overlay { get; }
    public System.Windows.Shapes.Path BubbleFillPath { get; }
    public System.Windows.Shapes.Path BubbleOutlinePath { get; }
    // 크롭 OFF(칸 밖으로 넘치는) 말풍선을 담는, 칸 안의 비클리핑 오버레이.
    public Canvas FreeOverlay { get; }
    public System.Windows.Shapes.Path FreeBubbleFillPath { get; }
    public System.Windows.Shapes.Path FreeBubbleOutlinePath { get; }
    public Thumb ResizeHandle { get; }
    public List<PanelImage> Images { get; } = new();
    public PanelImage? SelectedImage { get; set; }
    public List<SpeechBubble> Bubbles { get; } = new();
    public bool IsLocked { get; set; }

    // 칸 모양(사변형). 기본 모서리 변위 0 = 직사각형. QuadFill=흰 배경, QuadBorder=검은 외곽선.
    public System.Windows.Shapes.Path QuadFill { get; set; } = null!;
    // 칸 테두리(변마다 두께 보정). 인덱스 0=상,1=우,2=하,3=좌.
    public System.Windows.Shapes.Line[] QuadBorderLines { get; set; } = System.Array.Empty<System.Windows.Shapes.Line>();
    // 크롭 OFF(넘치는) 이미지는 테두리보다 앞에 그리기 위해 별도 캔버스(테두리 위)에 둔다.
    public Canvas FreeImageCanvas { get; set; } = null!;
    public bool CornerMode { get; set; }
    // 직사각형 모서리(0=TL,1=TR,2=BR,3=BL) 기준 변위(px). 드래그로 기울어진 사변형을 만든다.
    public Point[] CornerOffsets { get; } = { new Point(), new Point(), new Point(), new Point() };

    public override string ToString()
    {
        return $"{(IsLocked ? "🔒 " : "")}{Number}번 칸";
    }
}

public enum MediaKind
{
    Static,    // 정지 이미지
    Animated,  // 움직이는 gif/webp (프레임 시퀀스)
    Video      // 동영상(MediaElement)
}

public sealed class PanelImage
{
    public PanelImage(
        ComicPanel ownerPanel,
        string path,
        MediaKind kind,
        Grid layer,
        FrameworkElement content,
        Image? image,
        MediaElement? media,
        Border selectionBorder,
        ScaleTransform scale,
        TranslateTransform translate)
    {
        OwnerPanel = ownerPanel;
        Path = path;
        Kind = kind;
        Layer = layer;
        Content = content;
        Image = image;
        Media = media;
        SelectionBorder = selectionBorder;
        Scale = scale;
        Translate = translate;
    }

    public ComicPanel OwnerPanel { get; }
    public string Path { get; }
    public MediaKind Kind { get; }
    public Grid Layer { get; }
    // 실제 화면 요소(정지/애니 = Image, 동영상 = MediaElement). 크기·변환·히트테스트에 쓴다.
    public FrameworkElement Content { get; }
    public Image? Image { get; }
    public MediaElement? Media { get; }
    public Border SelectionBorder { get; }
    public ScaleTransform Scale { get; }
    public TranslateTransform Translate { get; }
    public bool IsCropped { get; set; } = true;
    public bool IsLocked { get; set; }

    // 칸 리사이즈 시 따라갈 기준점(0~1). X: 0=좌, 1=우 / Y: 0=하, 1=상. 기본 (0,1)=좌상단 고정.
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;

    // 픽셀 알파 히트테스트용 BGRA 변환본 캐시. Key가 현재 소스와 같으면 Value를 재사용한다
    // (애니메이션/동영상 프레임 교체 시 소스 참조가 바뀌면 자동으로 다시 만든다).
    public BitmapSource? AlphaCacheKey { get; set; }
    public BitmapSource? AlphaCacheValue { get; set; }

    // 움직이는 이미지용 프레임/지연(ms)과 재생 타이머.
    public BitmapSource[]? Frames { get; set; }
    public int[]? FrameDelays { get; set; }
    public DispatcherTimer? FrameTimer { get; set; }

    // 제거 시 재생 자원을 정리한다.
    public void StopPlayback()
    {
        FrameTimer?.Stop();
        FrameTimer = null;
        if (Media != null)
        {
            try { Media.Stop(); Media.Close(); } catch { /* 재생 중지 실패는 무시 */ }
            Media.Source = null;
        }
    }

    public override string ToString()
    {
        var index = OwnerPanel.Images.IndexOf(this) + 1;
        var tag = Kind == MediaKind.Video ? "🎞 " : Kind == MediaKind.Animated ? "▶ " : "";
        return $"{(IsLocked ? "🔒 " : "")}{tag}{index}번 이미지 - {System.IO.Path.GetFileName(Path)}";
    }
}

public sealed class SpeechBubble
{
    public SpeechBubble(
        ComicPanel ownerPanel,
        Border container,
        System.Windows.Shapes.Path bodyPath,
        System.Windows.Shapes.Path shapePath,
        OutlinedTextBlock textBlock,
        Border selectionBorder,
        Thumb resizeHandle)
    {
        OwnerPanel = ownerPanel;
        Container = container;
        BodyPath = bodyPath;
        ShapePath = shapePath;
        TextBlock = textBlock;
        SelectionBorder = selectionBorder;
        ResizeHandle = resizeHandle;
    }

    public ComicPanel OwnerPanel { get; }
    public Border Container { get; }
    public System.Windows.Shapes.Path BodyPath { get; }
    // 오버레이에 놓이는, 이 말풍선의 본체+꼬리 채움/외곽선 경로(꼬리도 같은 도형이라 배경색을 따라간다).
    public System.Windows.Shapes.Path ShapePath { get; }
    // 속도선: 선마다 개별 페이드를 주기 위해 컨테이너 안에 두는 선 호스트(선마다 Path 1개).
    public Canvas LineHost { get; set; } = null!;
    public OutlinedTextBlock TextBlock { get; }
    public Border SelectionBorder { get; }
    public Thumb ResizeHandle { get; }
    public bool IsCropped { get; set; } = true;
    public bool IsLocked { get; set; }
    public BubbleShape Shape { get; set; } = BubbleShape.RoundRect;
    public int ShapeCount { get; set; } = 9;
    public double ShapeStrength { get; set; }
    // 말풍선 배경색(채움). 기본 흰색.
    public Brush BackgroundBrush { get; set; } = Brushes.White;
    // 선 호스트(집중선/속도선)를 마지막으로 만들 때의 파라미터 서명. 위치만 바뀐 경우 재생성을 건너뛴다.
    public string? LineHostSignature { get; set; }
    public List<BubbleTail> Tails { get; } = new();
    public double RelativeX { get; set; }
    public double RelativeY { get; set; }
    // 칸 리사이즈 시 따라갈 기준점(0~1). X: 0=좌, 1=우 / Y: 0=하, 1=상. 기본 (0,1)=좌상단 고정.
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;

    public override string ToString()
    {
        var index = OwnerPanel.Bubbles.IndexOf(this) + 1;
        var prefix = IsLocked ? "🔒 " : "";
        var preview = TextBlock.Text.ReplaceLineEndings(" ").Trim();

        if (preview.Length > 18)
        {
            preview = preview[..18] + "...";
        }

        return string.IsNullOrWhiteSpace(preview)
            ? $"{prefix}{index}번 말풍선"
            : $"{prefix}{index}번 말풍선 - {preview}";
    }
}

// 글자에 색 아웃라인(테두리)을 그릴 수 있는 텍스트 요소. 기본 TextBlock 대용으로 쓴다.
public sealed class OutlinedTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineEnabledProperty = DependencyProperty.Register(
        nameof(OutlineEnabled), typeof(bool), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextWrapping.NoWrap, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool OutlineEnabled { get => (bool)GetValue(OutlineEnabledProperty); set => SetValue(OutlineEnabledProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    private FormattedText CreateFormattedText(double maxWidth)
    {
        var typeface = new Typeface(FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeight, FontStretches.Normal);
        var ft = new FormattedText(
            Text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1, FontSize),
            Fill ?? Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment
        };

        if (TextWrapping != TextWrapping.NoWrap && maxWidth > 0 && !double.IsInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
        }

        return ft;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pad = Padding;
        var maxWidth = availableSize.Width - pad.Left - pad.Right;
        var ft = CreateFormattedText(maxWidth);
        return new Size(ft.Width + pad.Left + pad.Right, ft.Height + pad.Top + pad.Bottom);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var pad = Padding;
        var maxWidth = Math.Max(0, ActualWidth - pad.Left - pad.Right);
        var ft = CreateFormattedText(maxWidth);
        var geometry = ft.BuildGeometry(new Point(pad.Left, pad.Top));

        if (OutlineEnabled && Stroke != null)
        {
            var penWidth = Math.Max(3, FontSize / 3.5);
            var pen = new Pen(Stroke, penWidth)
            {
                LineJoin = PenLineJoin.Round,
                MiterLimit = 2
            };
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        drawingContext.DrawGeometry(Fill, null, geometry);
    }
}

public sealed class BubbleTail
{
    public double StartX { get; set; } = 85;
    public double StartY { get; set; } = 50;
    public double MidX { get; set; } = 107;
    public double MidY { get; set; } = 90;
    public double X { get; set; } = 130;
    public double Y { get; set; } = 130;
    public double Width { get; set; } = 28;
    // 이 꼬리를 본체에서 안으로 깎을지(개별 적용).
    public bool TailInward { get; set; }

    public override string ToString()
    {
        return $"{(TailInward ? "↩ " : "")}꼬리 ({X:0}, {Y:0})";
    }
}

public enum BubbleShape
{
    RoundRect,
    CloudExplosion,
    Shout,
    Flash,
    ConcentrationLines,
    EffectLines,
    None
}

public enum SelectionKind
{
    None,
    Panel,
    Image,
    Bubble
}

public enum TailPointKind
{
    Start,
    Mid,
    End
}

public enum TextRegionCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}
