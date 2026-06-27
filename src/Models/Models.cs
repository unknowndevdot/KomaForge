using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KomaForge;

public sealed class ComicPanel : System.ComponentModel.INotifyPropertyChanged
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
    // 세션 내 안정적 고유 ID(실행취소/다시실행 후 같은 오브젝트를 다시 선택하기 위함). 저장에도 round-trip.
    public string Id { get; set; } = string.Empty;

    // 사용자 지정 칸 이름(비어 있으면 기본 "N번 칸"으로 표시). 저장에 round-trip.
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); OnChanged(nameof(DisplayText)); } }
    }

    // 리스트에서 인라인 이름 편집 중인지(UI 전용).
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing != value) { _isEditing = value; OnChanged(nameof(IsEditing)); } }
    }

    // 칸 리스트 표시 텍스트(잠금 아이콘 + 이름/기본 번호).
    public string DisplayText => $"{(IsLocked ? "🔒 " : "")}{(string.IsNullOrWhiteSpace(Name) ? $"{Number}번 칸" : Name)}";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

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
    // 파일이 없어 런타임으로 만들지 못한 이미지의 원본 데이터(로드 시 보관). 저장 시 다시 합쳐 영구 삭제를 막는다.
    // 파일이 복구되면 다음 로드에서 정상 이미지로 되살아난다.
    public List<PanelImageData> UnresolvedImages { get; } = new();
    public List<SpeechBubble> Bubbles { get; } = new();
    public bool IsLocked { get; set; }

    // 칸 모양(사변형). 기본 모서리 변위 0 = 직사각형. QuadFill=배경, QuadBorder=외곽선.
    public System.Windows.Shapes.Path QuadFill { get; set; } = null!;
    // 칸 테두리(변마다 두께 보정). 인덱스 0=상,1=우,2=하,3=좌.
    public System.Windows.Shapes.Line[] QuadBorderLines { get; set; } = System.Array.Empty<System.Windows.Shapes.Line>();
    // 칸 테두리색(기본 검정).
    public Color BorderColor { get; set; } = Colors.Black;
    // 말풍선 외곽선을 테두리색별로 합쳐 그린 동적 경로들(색이 다른 말풍선은 따로 그린다).
    public List<System.Windows.Shapes.Path> DynamicBubbleOutlines { get; } = new();
    // 크롭 OFF(넘치는) 이미지는 테두리보다 앞에 그리기 위해 별도 캔버스(테두리 위)에 둔다.
    public Canvas FreeImageCanvas { get; set; } = null!;
    public bool CornerMode { get; set; }
    // 직사각형 모서리(0=TL,1=TR,2=BR,3=BL) 기준 변위(px). 드래그로 기울어진 사변형을 만든다.
    public Point[] CornerOffsets { get; } = { new Point(), new Point(), new Point(), new Point() };

    public override string ToString() => DisplayText;
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
    // 세션 내 안정적 고유 ID(실행취소/다시실행 후 재선택용). 저장에도 round-trip.
    public string Id { get; set; } = string.Empty;
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

    // 가장자리 그라데이션. 선택한 방향 변이 '대상 색'으로, 반대편은 원본 이미지로 보간된다.
    // 대상 색의 알파가 0(투명)이면 색을 칠하는 대신 이미지가 점점 사라진다(페이지가 비침). None이면 효과 없음.
    public ImageGradientDirection GradientDirection { get; set; } = ImageGradientDirection.None;
    public Color GradientColor { get; set; } = Color.FromArgb(0, 255, 255, 255); // 기본 투명(=사라짐)
    // 페이드 구간(%): 대상(방향) 변(0%)에서 GradientStart까지 완전 대상색,
    // GradientEnd 이후 완전 원본. 그 사이 선형 보간.
    public double GradientStart { get; set; } = 40;
    public double GradientEnd { get; set; } = 60;
    // 색 모드일 때 이미지 위에 칠하는 오버레이(이미지 모양 마스크 적용). 콘텐츠와 같은 변환 공유.
    public Border? GradientOverlay { get; set; }

    // 픽셀 알파 히트테스트용 BGRA 변환본 캐시. Key가 현재 소스와 같으면 Value를 재사용한다
    // (애니메이션/동영상 프레임 교체 시 소스 참조가 바뀌면 자동으로 다시 만든다).
    public BitmapSource? AlphaCacheKey { get; set; }
    public BitmapSource? AlphaCacheValue { get; set; }

    // 움직이는 이미지: 재생 타이머 + 프레임을 그때그때 디코드하는 스트리밍 플레이어.
    public DispatcherTimer? FrameTimer { get; set; }
    public AnimatedPlayer? Player { get; set; }
    // 출력 설정(애니/동영상). 0이면 원본 기준 자동. OutputDuration=한 바퀴 길이(초), OutputFps=내보내기 프레임레이트.
    public double OutputDuration { get; set; }
    public double OutputFps { get; set; }

    // 제거 시 재생 자원을 정리한다(타이머·플레이어 코덱·동영상).
    public void StopPlayback()
    {
        FrameTimer?.Stop();
        FrameTimer = null;
        Player?.Dispose();
        Player = null;
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

// 파일이 없어 못 띄운 이미지를 이미지 리스트에 표시·삭제하기 위한 항목(원본 데이터·소유 칸을 함께 보관).
public sealed class UnresolvedImageItem
{
    public ComicPanel Panel { get; }
    public PanelImageData Data { get; }

    public UnresolvedImageItem(ComicPanel panel, PanelImageData data)
    {
        Panel = panel;
        Data = data;
    }

    public override string ToString() => $"⚠ (파일 없음) {System.IO.Path.GetFileName(Data.Path)}";
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
    // 세션 내 안정적 고유 ID(실행취소/다시실행 후 재선택용). 저장에도 round-trip.
    public string Id { get; set; } = string.Empty;
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
    // 불규칙도(0~100): 0이면 모양이 균일, 높을수록 들쭉날쭉해진다. 50이 기존 기본 흔들림.
    public double ShapeIrregularity { get; set; } = 50;
    // 폭 불규칙도(0~100, 구름/폭발 전용): 상하좌우(변 중앙)를 바깥으로 넓혀 모서리가 패인 모양을 만든다. 0이면 효과 없음.
    public double ShapeWidthVariation { get; set; }
    // 속도선: 각 선을 양쪽 끝 모두 투명하게 페이드할지(기본 OFF = 한쪽만 투명해짐).
    public bool LineFadeBothSides { get; set; }
    // 말풍선 배경색(채움). 기본 흰색.
    public Brush BackgroundBrush { get; set; } = Brushes.White;
    // 말풍선 테두리(외곽선) 색. 기본 검정.
    public Color BorderColor { get; set; } = Colors.Black;
    // 선 호스트(집중선/속도선)를 마지막으로 만들 때의 파라미터 서명. 위치만 바뀐 경우 재생성을 건너뛴다.
    public string? LineHostSignature { get; set; }
    public List<BubbleTail> Tails { get; } = new();
    public double RelativeX { get; set; }
    public double RelativeY { get; set; }
    // 칸 리사이즈 시 따라갈 기준점(0~1). X: 0=좌, 1=우 / Y: 0=하, 1=상. 기본 (0,1)=좌상단 고정.
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;
    // 사용자가 지정한 글자 크기(= 최대). 실제 렌더 크기(TextBlock.FontSize)는 말풍선이 작으면 이 값 이하로 자동 축소된다.
    public double MaxFontSize { get; set; } = 18;
    // 모서리 조절(사변형 일그러뜨림) 변위. 칸과 동일하게 TL,TR,BR,BL 순서. 기본 0이면 일그러짐 없음.
    public Point[] CornerOffsets { get; } = { new Point(), new Point(), new Point(), new Point() };
    // 모서리 조절을 도형(본체·꼬리·외곽선)에 적용할지.
    public bool WarpShape { get; set; }
    // 모서리 조절을 안의 글자에 적용할지.
    public bool WarpText { get; set; }
    // 글자 회전 각도(도, 0~360). 0이면 회전 없음. 선효과(속도선·집중선)를 제외한 모든 말풍선에 적용. 글자 요소 중심을 기준으로 돈다.
    public double TextRotation { get; set; }

    public override string ToString()
    {
        var prefix = IsLocked ? "🔒 " : "";
        var shapeName = ShapeDisplayName(Shape);
        var preview = TextBlock.Text.ReplaceLineEndings(" ").Trim();

        if (preview.Length > 18)
        {
            preview = preview[..18] + "...";
        }

        return string.IsNullOrWhiteSpace(preview)
            ? $"{prefix}{shapeName}"
            : $"{prefix}{shapeName} - {preview}";
    }

    // 모양 → 리스트 표시용 한글 이름(인스펙터 모양 콤보 라벨과 동일).
    private static string ShapeDisplayName(BubbleShape shape) => shape switch
    {
        BubbleShape.RoundRect => "원형/사각",
        BubbleShape.CloudExplosion => "구름/폭발",
        BubbleShape.Flash => "플래시",
        BubbleShape.ConcentrationLines => "집중선",
        BubbleShape.EffectLines => "속도선",
        BubbleShape.None => "테두리 없음",
        _ => "말풍선"
    };
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

    // 모서리 조절(사변형 워프): 4개 모서리 변위(TL,TR,BR,BL). null이면 워프 없음.
    public static readonly DependencyProperty WarpOffsetsProperty = DependencyProperty.Register(
        nameof(WarpOffsets), typeof(Point[]), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // 워프 기준이 되는 컨테이너(말풍선) 크기. 글자 요소는 여백만큼 안쪽에 놓이므로 함께 필요.
    public static readonly DependencyProperty WarpContainerSizeProperty = DependencyProperty.Register(
        nameof(WarpContainerSize), typeof(Size), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(new Size(0, 0), FrameworkPropertyMetadataOptions.AffectsRender));

    public Point[]? WarpOffsets { get => (Point[]?)GetValue(WarpOffsetsProperty); set => SetValue(WarpOffsetsProperty, value); }
    public Size WarpContainerSize { get => (Size)GetValue(WarpContainerSizeProperty); set => SetValue(WarpContainerSizeProperty, value); }

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

    // 구간별 서식(글자색·글씨체·아웃라인색). 이어 붙이면 Text와 같아야 한다. null/빈 항목은 기본값 상속.
    // 색/아웃라인 변형이 있으면 채움색별 그룹으로 렌더하고, 글꼴만 다르면 단일 경로에서도 반영된다.
    private List<FlowTextRun>? _styledRuns;
    public List<FlowTextRun>? StyledRuns
    {
        get => _styledRuns;
        set { _styledRuns = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    // 줄간격(px). 0이면 글꼴 기본 줄간격.
    private double _lineHeight;
    public double LineHeight
    {
        get => _lineHeight;
        set { _lineHeight = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    private FormattedText BuildFormattedText(double fontSize, double maxWidth)
    {
        var typeface = new Typeface(FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeight, FontStretches.Normal);
        var ft = new FormattedText(
            Text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1, fontSize),
            Fill ?? Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment
        };

        if (TextWrapping != TextWrapping.NoWrap && maxWidth > 0 && !double.IsInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
        }

        if (_lineHeight > 0)
        {
            ft.LineHeight = _lineHeight; // 0/미지정이면 글꼴 기본 줄간격.
        }

        ApplyRunFonts(ft);
        return ft;
    }

    private FormattedText CreateFormattedText(double maxWidth) => BuildFormattedText(FontSize, maxWidth);

    // 구간별 글꼴을 FormattedText의 문자 범위에 적용한다(측정·렌더 공용). 색/아웃라인은 그룹 렌더에서 처리.
    private void ApplyRunFonts(FormattedText ft)
    {
        var runs = _styledRuns;
        if (runs == null || runs.Count == 0)
        {
            return;
        }

        var total = (Text ?? string.Empty).Length;
        var pos = 0;
        foreach (var r in runs)
        {
            var len = r.Text.Length;
            if (len <= 0 || pos >= total)
            {
                pos += len;
                continue;
            }
            var start = pos;
            pos += len;
            var count = Math.Min(len, total - start);
            if (!string.IsNullOrEmpty(r.FontFamily))
            {
                try { ft.SetFontFamily(new FontFamily(r.FontFamily), start, count); } catch { /* 알 수 없는 글꼴 무시 */ }
            }
        }
    }

    // 구간마다 고유 키색을 전경색으로 부여하고 키색 → (실제 채움,아웃라인) 매핑을 만든다.
    // 같은 채움색이라도 아웃라인이 다르면 다른 키 → 다른 글리프런으로 분리되어 독립 렌더된다(키색은 그리지 않음).
    private void AssignRunKeys(FormattedText ft, Dictionary<Color, (Color Fill, Color Outline)> keyToPair)
    {
        var runs = _styledRuns;
        if (runs == null)
        {
            return;
        }

        var baseFill = (Fill as SolidColorBrush)?.Color ?? Colors.Black;
        var baseStroke = (Stroke as SolidColorBrush)?.Color ?? Colors.Transparent;
        var total = (Text ?? string.Empty).Length;
        var pos = 0;
        var pairKeys = new Dictionary<(Color, Color), Color>();
        var seq = 0;
        foreach (var r in runs)
        {
            var len = r.Text.Length;
            if (len <= 0 || pos >= total)
            {
                pos += len;
                continue;
            }
            var start = pos;
            pos += len;
            var count = Math.Min(len, total - start);
            var fill = string.IsNullOrEmpty(r.Color) ? baseFill : ParseHexColor(r.Color, baseFill);
            var outline = string.IsNullOrEmpty(r.OutlineColor) ? baseStroke : ParseHexColor(r.OutlineColor, baseStroke);
            var pk = (fill, outline);
            if (!pairKeys.TryGetValue(pk, out var key))
            {
                key = Color.FromArgb(255, (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq);
                seq++;
                pairKeys[pk] = key;
                keyToPair[key] = (fill, outline);
            }
            try { ft.SetForegroundBrush(new SolidColorBrush(key), start, count); } catch { /* 범위 오류 무시 */ }
        }
    }

    private static Color ParseHexColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    // 지정한 글자 크기로 줄바꿈(maxWidth)했을 때의 텍스트 크기를 잰다(자동 축소 계산용). 구간별 글꼴 반영.
    public Size MeasureAtFont(double fontSize, double maxWidth)
    {
        var ft = BuildFormattedText(fontSize, maxWidth);
        return new Size(ft.Width, ft.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pad = Padding;
        var maxWidth = availableSize.Width - pad.Left - pad.Right;
        var ft = CreateFormattedText(maxWidth);
        return new Size(ft.Width + pad.Left + pad.Right, ft.Height + pad.Top + pad.Bottom);
    }

    // WPF 기본 동작: 콘텐츠가 레이아웃 슬롯(=텍스트 영역)보다 크면 자동으로 그 경계로 잘라낸다(layout clip).
    // 말풍선 글자는 영역을 넘쳐도(작은 영역·워프 등) 잘리지 않게 이 자동 클립을 끈다.
    // (칸 크롭은 panel.Overlay.Clip이 별도로 처리하므로 영향 없음.)
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var pad = Padding;
        var maxWidth = Math.Max(0, ActualWidth - pad.Left - pad.Right);
        var ft = CreateFormattedText(maxWidth);
        var origin = new Point(pad.Left, pad.Top);

        var offs = WarpOffsets;
        var warpActive = offs != null && offs.Length == 4 && WarpHasOffset(offs)
                         && WarpContainerSize.Width > 0 && WarpContainerSize.Height > 0;

        // 구간별 색/아웃라인이 있으면 채움색별 그룹으로 그린다(글꼴은 ft에 이미 반영됨).
        if (NeedGroupedRender())
        {
            RenderGroupedRuns(drawingContext, ft, origin, warpActive);
            return;
        }

        // 단일 경로: 한 색/한 아웃라인(구간별 글꼴은 ft에 반영되어 BuildGeometry에 포함).
        var geometry = ft.BuildGeometry(origin);
        if (warpActive)
        {
            geometry = WarpTextGeometry(geometry, offs!);
        }

        // 아웃라인 색이 불투명할 때만 그린다(투명 = 아웃라인 없음). 별도 ON/OFF 없이 색으로만 제어.
        if (Stroke is SolidColorBrush strokeBrush && strokeBrush.Color.A > 0)
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

    private bool NeedGroupedRender()
    {
        var runs = _styledRuns;
        if (runs == null)
        {
            return false;
        }
        foreach (var r in runs)
        {
            if (!string.IsNullOrEmpty(r.Color) || !string.IsNullOrEmpty(r.OutlineColor))
            {
                return true;
            }
        }
        return false;
    }

    // 구간별 채움/아웃라인 렌더: ft를 비주얼에 그려 글리프(런별 전경색 포함)를 얻고, 채움색별로 묶어
    // 1패스 외곽선(채움색→아웃라인색 매핑) + 2패스 채움으로 그린다. 워프도 글리프별로 적용.
    private void RenderGroupedRuns(DrawingContext dc, FormattedText ft, Point origin, bool warpActive)
    {
        // 구간마다 고유 키색을 전경색으로 부여한 뒤, 그 글리프를 키색별로 묶어 실제 (채움,아웃라인)으로 그린다.
        var keyToPair = new Dictionary<Color, (Color Fill, Color Outline)>();
        AssignRunKeys(ft, keyToPair);

        var temp = new DrawingVisual();
        using (var c = temp.RenderOpen())
        {
            c.DrawText(ft, origin);
        }

        var byColor = new Dictionary<Color, GeometryGroup>();
        var dg = VisualTreeHelper.GetDrawing(temp);
        if (dg != null)
        {
            CollectRunGlyphs(dg, Matrix.Identity, byColor, warpActive);
        }
        if (byColor.Count == 0)
        {
            return;
        }

        var baseFill = (Fill as SolidColorBrush)?.Color ?? Colors.Black;
        var baseStroke = (Stroke as SolidColorBrush)?.Color ?? Colors.Transparent;
        var penWidth = Math.Max(3, FontSize / 3.5);

        // 1패스: 외곽선(모든 글자 뒤로).
        foreach (var kv in byColor)
        {
            var pair = keyToPair.TryGetValue(kv.Key, out var p) ? p : (baseFill, baseStroke);
            if (pair.Item2.A == 0)
            {
                continue;
            }
            var pen = new Pen(new SolidColorBrush(pair.Item2), penWidth) { LineJoin = PenLineJoin.Round, MiterLimit = 2 };
            dc.DrawGeometry(null, pen, kv.Value);
        }
        // 2패스: 채움(각 구간의 실제 채움색).
        foreach (var kv in byColor)
        {
            var pair = keyToPair.TryGetValue(kv.Key, out var p) ? p : (baseFill, baseStroke);
            dc.DrawGeometry(new SolidColorBrush(pair.Item1), null, kv.Value);
        }
    }

    private void CollectRunGlyphs(Drawing d, Matrix m, Dictionary<Color, GeometryGroup> acc, bool warpActive)
    {
        switch (d)
        {
            case DrawingGroup dg:
                var gm = m;
                if (dg.Transform != null && !dg.Transform.Value.IsIdentity)
                {
                    gm = dg.Transform.Value * m;
                }
                foreach (var child in dg.Children)
                {
                    CollectRunGlyphs(child, gm, acc, warpActive);
                }
                break;

            case GlyphRunDrawing grd when grd.GlyphRun != null:
                Geometry geo = grd.GlyphRun.BuildGeometry();
                if (geo == null || geo.IsEmpty())
                {
                    break;
                }
                if (!m.IsIdentity)
                {
                    geo.Transform = new MatrixTransform(m);
                }
                if (warpActive)
                {
                    geo = WarpTextGeometry(geo, WarpOffsets!);
                }
                var color = (grd.ForegroundBrush as SolidColorBrush)?.Color
                            ?? ((Fill as SolidColorBrush)?.Color ?? Colors.Black);
                if (!acc.TryGetValue(color, out var grp))
                {
                    grp = new GeometryGroup { FillRule = FillRule.Nonzero };
                    acc[color] = grp;
                }
                grp.Children.Add(geo);
                break;
        }
    }

    // 글리프 도형을 잘게 직선화한 뒤, 글자 요소 로컬 좌표를 '컨테이너 좌표 → 사변형 워프 → 다시 요소 로컬'로 옮긴다.
    private Geometry WarpTextGeometry(Geometry geometry, Point[] offs)
    {
        var cw = WarpContainerSize.Width;
        var ch = WarpContainerSize.Height;
        var m = Margin;
        // 글자 요소의 컨테이너 내 좌상단(가로는 stretch라 Margin.Left, 세로는 세로 맞춤에 따라 보정).
        var ox = m.Left;
        var slotH = ch - m.Top - m.Bottom;
        var extra = Math.Max(0, slotH - ActualHeight);
        var oy = VerticalAlignment switch
        {
            System.Windows.VerticalAlignment.Top => m.Top,
            System.Windows.VerticalAlignment.Bottom => m.Top + extra,
            _ => m.Top + extra / 2 // Center/Stretch
        };

        var flat = geometry.GetFlattenedPathGeometry(0.2, ToleranceType.Absolute);
        var result = new PathGeometry { FillRule = flat.FillRule };
        foreach (var fig in flat.Figures)
        {
            var nf = new PathFigure
            {
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled,
                StartPoint = WarpLocal(fig.StartPoint, ox, oy, cw, ch, offs)
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pls)
                {
                    var pts = new PointCollection();
                    foreach (var p in pls.Points)
                    {
                        pts.Add(WarpLocal(p, ox, oy, cw, ch, offs));
                    }
                    nf.Segments.Add(new PolyLineSegment(pts, seg.IsStroked));
                }
                else if (seg is LineSegment ls)
                {
                    nf.Segments.Add(new LineSegment(WarpLocal(ls.Point, ox, oy, cw, ch, offs), ls.IsStroked));
                }
            }
            result.Figures.Add(nf);
        }
        return result;
    }

    private static Point WarpLocal(Point p, double ox, double oy, double cw, double ch, Point[] o)
    {
        var w = WarpBilinear(ox + p.X, oy + p.Y, cw, ch, o);
        return new Point(w.X - ox, w.Y - oy);
    }

    private static Point WarpBilinear(double x, double y, double w, double h, Point[] o)
    {
        var u = w > 0 ? x / w : 0;
        var v = h > 0 ? y / h : 0;
        var tlX = o[0].X;          var tlY = o[0].Y;
        var trX = w + o[1].X;      var trY = o[1].Y;
        var brX = w + o[2].X;      var brY = h + o[2].Y;
        var blX = o[3].X;          var blY = h + o[3].Y;
        var nx = (1 - u) * (1 - v) * tlX + u * (1 - v) * trX + u * v * brX + (1 - u) * v * blX;
        var ny = (1 - u) * (1 - v) * tlY + u * (1 - v) * trY + u * v * brY + (1 - u) * v * blY;
        return new Point(nx, ny);
    }

    private static bool WarpHasOffset(Point[] o)
        => o[0].X != 0 || o[0].Y != 0 || o[1].X != 0 || o[1].Y != 0
           || o[2].X != 0 || o[2].Y != 0 || o[3].X != 0 || o[3].Y != 0;
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
    // 생각 말풍선 꼬리: 곡선 대신 점점 작아지는 원 3개로 표시(개별 적용).
    public bool ThoughtTail { get; set; }

    public override string ToString()
    {
        return $"{(ThoughtTail ? "○ " : TailInward ? "↩ " : "")}꼬리 ({X:0}, {Y:0})";
    }
}

public enum BubbleShape
{
    RoundRect,
    CloudExplosion,
    Flash,
    ConcentrationLines,
    EffectLines,
    None
}

// 이미지 투명도 그라데이션 방향(선택한 변이 투명해진다).
public enum ImageGradientDirection
{
    None,
    Top,
    Bottom,
    Left,
    Right
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
