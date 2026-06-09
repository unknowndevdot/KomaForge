namespace KomaForge;

public sealed class ComicProjectData
{
    public string Title { get; set; } = string.Empty;
    public double AutoMargin { get; set; } = 24;
    public double AutoGutter { get; set; } = 14;
    public int CurrentPageIndex { get; set; }
    public List<ComicPageData> Pages { get; set; } = new();
}

public sealed class ComicPageData
{
    public string Name { get; set; } = "Page";
    public double PageWidth { get; set; } = 820;
    public double PageHeight { get; set; } = 1120;
    public bool BlackBackground { get; set; }
    public List<ComicPanelData> Panels { get; set; } = new();
}

public sealed class ComicPanelData
{
    public int Number { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsLocked { get; set; }
    public bool CornerMode { get; set; }
    // 사변형 모서리 변위(TL,TR,BR,BL × X,Y) = 8개. 기본 0이면 직사각형.
    public double[] CornerOffsets { get; set; } = new double[8];
    public List<PanelImageData> Images { get; set; } = new();
    public List<SpeechBubbleData> Bubbles { get; set; } = new();
}

public sealed class PanelImageData
{
    public string Path { get; set; } = string.Empty;
    public double Scale { get; set; } = 1;
    public double TranslateX { get; set; }
    public double TranslateY { get; set; }
    public bool IsCropped { get; set; } = true;
    public bool IsLocked { get; set; }
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;
}

public sealed class SpeechBubbleData
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 170;
    public double Height { get; set; } = 100;
    public double FontSize { get; set; } = 18;
    public double TextMarginLeft { get; set; } = 16;
    public double TextMarginTop { get; set; } = 12;
    public double TextMarginRight { get; set; } = 16;
    public double TextMarginBottom { get; set; } = 12;
    public bool IsCropped { get; set; }
    public bool IsLocked { get; set; }
    public bool HasTextOutline { get; set; }
    public string FillColor { get; set; } = "#000000";
    public string StrokeColor { get; set; } = "#FFFFFF";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string Shape { get; set; } = nameof(BubbleShape.RoundRect);
    public int ShapeCount { get; set; } = 9;
    public double ShapeStrength { get; set; }
    public bool TailInward { get; set; }
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;
    public List<BubbleTailData> Tails { get; set; } = new();
}

public sealed class BubbleTailData
{
    public double StartX { get; set; } = 85;
    public double StartY { get; set; } = 50;
    // 구버전 저장 파일에는 Mid 값이 없으므로 NaN을 기본값으로 두고,
    // 불러올 때 NaN이면 시작점과 끝점의 중점으로 계산한다.
    public double MidX { get; set; } = double.NaN;
    public double MidY { get; set; } = double.NaN;
    public double X { get; set; } = 130;
    public double Y { get; set; } = 130;
    public double Width { get; set; } = 28;
    public bool TailInward { get; set; }
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    // 프로젝트와 무관한 앱 설정(다음 실행 시 복원).
    public bool PageFit { get; set; }
    public string LayoutPattern { get; set; } = "1,2,1";
    public string AutoMargin { get; set; } = "24";
    public string AutoGutter { get; set; } = "14";
    public string BubbleShape { get; set; } = "Oval";
    public bool InspectorVisible { get; set; } = true;
}
