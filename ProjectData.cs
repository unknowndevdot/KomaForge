namespace KomaForge;

public sealed class ComicProjectData
{
    public string Title { get; set; } = string.Empty;
    public double AutoMargin { get; set; } = 24;
    public double AutoGutter { get; set; } = 14;
    public int CurrentPageIndex { get; set; }
    public List<ComicPageData> Pages { get; set; } = new();
}

public sealed class ComicPageData : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "Page";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } }
    }

    // 리스트에서 인라인 이름 편집 중인지(저장 안 함, UI 전용).
    private bool _isEditing;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing != value) { _isEditing = value; OnChanged(nameof(IsEditing)); } }
    }

    public double PageWidth { get; set; } = 832;
    public double PageHeight { get; set; } = 1216;
    public bool BlackBackground { get; set; }
    public List<ComicPanelData> Panels { get; set; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class ComicPanelData
{
    public int Number { get; set; }
    public string Id { get; set; } = string.Empty;
    // 사용자 지정 칸 이름(비어 있으면 기본 "N번 칸").
    public string Name { get; set; } = string.Empty;
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
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public double Scale { get; set; } = 1;
    // 세로 배율(비율 미유지 자유 리사이즈로 가로/세로가 달라질 수 있음). 0/미지정이면 Scale과 동일.
    public double ScaleY { get; set; }
    public double TranslateX { get; set; }
    public double TranslateY { get; set; }
    public bool IsCropped { get; set; } = true;
    public bool IsLocked { get; set; }
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;
}

public sealed class SpeechBubbleData
{
    public string Id { get; set; } = string.Empty;
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
    // 불규칙도(0~100). 0/미지정이면 구버전 호환을 위해 50(기존 기본 흔들림)으로 본다.
    public double ShapeIrregularity { get; set; } = 50;
    // 폭 불규칙도(0~100, 구름/폭발 전용). 0/미지정이면 효과 없음.
    public double ShapeWidthVariation { get; set; }
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
    // 창 상태("Normal"/"Maximized"). Normal일 때 Left/Top/Width/Height는 스냅 포함 실제 영역.
    public string WindowState { get; set; } = "Normal";
    // 프로젝트와 무관한 앱 설정(다음 실행 시 복원).
    public bool PageFit { get; set; }
    public string LayoutPattern { get; set; } = "1,2,1";
    public string AutoMargin { get; set; } = "24";
    public string AutoGutter { get; set; } = "14";
    public string BubbleShape { get; set; } = "Oval";
    public bool InspectorVisible { get; set; } = true;
    // 선택 미리보기 강조(마우스를 올린 곳에서 클릭 시 선택될 오브젝트를 미리 강조). 기본 OFF.
    public bool SelectionPreview { get; set; }
    // 이미지 크기 조절 시 항상 비율 유지. OFF면 자유 리사이즈(Shift로 일시 비율 유지). 기본 ON.
    public bool KeepAspectRatio { get; set; } = true;
    // 사용자 지정 단축키(액션 id → "Ctrl+S" 같은 표기). 없는 항목은 기본값을 쓴다.
    public Dictionary<string, string>? Shortcuts { get; set; }
    // 색 선택기에서 고른 최근 임의 색(최신순, hex).
    public List<string>? RecentColors { get; set; }
}
