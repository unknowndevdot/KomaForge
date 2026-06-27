namespace KomaForge;

public sealed class ComicProjectData
{
    public string Title { get; set; } = string.Empty;
    public double AutoMargin { get; set; } = 24;
    public double AutoGutter { get; set; } = 14;
    public int CurrentPageIndex { get; set; }
    public List<ComicPageData> Pages { get; set; } = new();
    // 프로젝트 전체에 걸쳐 페이지별로 자동 분할(노벨 뷰어) 표시되는 본문 텍스트.
    public FlowTextData FlowText { get; set; } = new();
    // 비주얼 노벨 생성용 템플릿 페이지 목록(일반 페이지와 별개). 스크립트 각 줄을 이 템플릿에 복제·치환해 페이지를 만든다.
    public List<ComicPageData> VnTemplates { get; set; } = new();
    // 자동저장/실행취소 시 편집 중이던 템플릿 인덱스(-1이면 일반 페이지 편집). 복원 시 편집 대상을 되살린다.
    public int VnEditingIndex { get; set; } = -1;
}

// 여러 페이지에 흘려 보여줄 본문 텍스트와 서식. 분할은 페이지 크기·이 서식으로 계산된다.
public sealed class FlowTextData
{
    // 비주얼 노벨 모드 ON/OFF. OFF면 본문을 페이지에 표시하지 않고 인스펙터 텍스트 섹션도 숨긴다.
    public bool Enabled { get; set; }
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Malgun Gothic"; // 빈 값이면 기본 글꼴. 기본은 말풍선과 동일.
    public double FontSize { get; set; } = 20;
    public double LineHeight { get; set; } = 30;            // 0이면 글꼴 기본 줄간격.
    public string Alignment { get; set; } = "Justify";      // Left/Center/Right/Justify.
    public double MarginLeft { get; set; } = 30;
    public double MarginTop { get; set; } = 30;
    public double MarginRight { get; set; } = 30;
    public double MarginBottom { get; set; } = 30;
    public string Color { get; set; } = "#FFFFFF";
    // 글자 아웃라인(외곽선) 색. 알파 0(투명)이면 외곽선 없음(말풍선과 동일하게 색으로만 제어).
    public string OutlineColor { get; set; } = "#000000";
    // 페이지보다 뒤에 깔리는 단일 배경색. 페이지 배경이 투명이면 이 색이 비쳐 보인다. 알파 0이면 없음.
    public string BackdropColor { get; set; } = "#FFFFFF";
    // 구간별 서식(글자색·글씨체·아웃라인색). 순서대로 본문 전체를 덮으며, 이어 붙이면 Text와 같다.
    // 비어 있으면(구버전) 불러올 때 Text 한 덩어리로 마이그레이션한다.
    public List<FlowTextRun> Runs { get; set; } = new();

    // Runs를 깊은 복사한다(MemberwiseClone은 리스트 참조를 공유해 실행취소가 오염되므로 직접 복사).
    public FlowTextData Clone()
    {
        var c = (FlowTextData)MemberwiseClone();
        c.Runs = Runs.Select(r => r.Clone()).ToList();
        return c;
    }
}

// 본문 텍스트의 한 구간(스팬). 색/글꼴/아웃라인이 null·빈 값이면 문서 기본값을 따른다.
public sealed class FlowTextRun
{
    public string Text { get; set; } = string.Empty;
    public string? FontFamily { get; set; }
    public string? Color { get; set; }
    public string? OutlineColor { get; set; }

    public FlowTextRun Clone() => (FlowTextRun)MemberwiseClone();
}

public sealed class ComicPageData : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "Page";
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnChanged(nameof(Name)); OnChanged(nameof(DisplayLabel)); } }
    }

    // 목록에 표시할 1-based 순번. UpdatePageList에서 위치에 맞춰 갱신한다(AlternationIndex는 가상화/재활용 시
    // 어긋나므로 쓰지 않는다). UI 전용, 저장 안 함.
    private int _displayIndex;
    [System.Text.Json.Serialization.JsonIgnore]
    public int DisplayIndex
    {
        get => _displayIndex;
        set { if (_displayIndex != value) { _displayIndex = value; OnChanged(nameof(DisplayIndex)); } }
    }

    // 비주얼 노벨 모드면 목록에 페이지 이름 대신 말풍선 텍스트 요약을 보여 준다(UI 전용, 저장 안 함).
    private bool _visualNovelMode;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool VisualNovelMode
    {
        get => _visualNovelMode;
        set { if (_visualNovelMode != value) { _visualNovelMode = value; OnChanged(nameof(DisplayLabel)); } }
    }

    // 페이지 목록 표시 라벨. 비주얼 노벨 모드면 말풍선 텍스트(순서대로 ' / '), 아니면 페이지 이름.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => _visualNovelMode ? BubbleSummary() : Name;

    // 비주얼 노벨 모드에서 현재 페이지 말풍선이 바뀌면 요약을 다시 계산하도록 알린다.
    public void RefreshDisplayLabel() => OnChanged(nameof(DisplayLabel));

    private string BubbleSummary()
    {
        var texts = new List<string>();
        foreach (var panel in Panels)
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
        return texts.Count > 0 ? string.Join(": ", texts) : string.Empty;
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
    // 구버전 호환: 옛 '페이지 배경 검은색' 플래그. 신버전은 BackgroundColor를 쓴다.
    public bool BlackBackground { get; set; }
    // 페이지 배경색(#RRGGBB). 빈 값이면 구버전 BlackBackground로 판단(검/흰).
    public string BackgroundColor { get; set; } = string.Empty;
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
    // 칸 배경색(#RRGGBB 또는 #AARRGGBB). 미지정이면 흰색.
    public string BackgroundColor { get; set; } = "#FFFFFF";
    // 칸 테두리색. 미지정이면 검정.
    public string BorderColor { get; set; } = "#000000";
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
    // 기준 콘텐츠 박스 크기(= 이미지를 추가한 시점의 칸 폭/높이). Scale·Translate가 이 박스를 기준으로 계산되므로
    // 반드시 함께 저장해야 한다. 0/미지정(구버전)이면 복원 시 현재 칸 크기로 폴백한다.
    public double BaseWidth { get; set; }
    public double BaseHeight { get; set; }
    public bool IsCropped { get; set; } = true;
    public bool IsLocked { get; set; }
    public double PivotX { get; set; }
    public double PivotY { get; set; } = 1;
    // 가장자리 그라데이션(투명 포함). "None"/미지정이면 효과 없음.
    public string GradientDirection { get; set; } = "None";
    // 대상 색(#AARRGGBB 또는 #RRGGBB). 알파 0이면 투명(=이미지 사라짐).
    public string GradientColor { get; set; } = "#00FFFFFF";
    public double GradientStart { get; set; } = 40;
    public double GradientEnd { get; set; } = 60;
    // 움직이는 이미지(애니/동영상)의 출력 설정. 0이면 원본 기준 자동. 라이브 재생·WebP 내보내기에 함께 적용.
    public double OutputDuration { get; set; } // 한 바퀴 길이(초).
    public double OutputFps { get; set; }      // 내보내기 출력 프레임레이트.
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
    // 글꼴 이름(시스템 글꼴). 빈 값이면 기본 글꼴.
    public string FontFamily { get; set; } = string.Empty;
    // 가로 맞춤(Left/Center/Right/Justify). 빈 값이면 가운데.
    public string TextAlignment { get; set; } = "Center";
    // 세로 맞춤(Top/Center/Bottom). 빈 값이면 가운데.
    public string VerticalAlignment { get; set; } = "Center";
    // 구간별 서식(글자색·글씨체·아웃라인색). 비어 있으면 Text 한 덩어리(단일 서식)로 본다.
    public List<FlowTextRun> Runs { get; set; } = new();
    // 줄간격(px). 0이면 글꼴 기본 줄간격.
    public double LineHeight { get; set; }
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
    // 말풍선 테두리(외곽선) 색. 미지정이면 검정.
    public string BorderColor { get; set; } = "#000000";
    public string Shape { get; set; } = nameof(BubbleShape.RoundRect);
    public int ShapeCount { get; set; } = 9;
    public double ShapeStrength { get; set; }
    // 불규칙도(0~100). 0/미지정이면 구버전 호환을 위해 50(기존 기본 흔들림)으로 본다.
    public double ShapeIrregularity { get; set; } = 50;
    // 폭 불규칙도(0~100, 구름/폭발 전용). 0/미지정이면 효과 없음.
    public double ShapeWidthVariation { get; set; }
    // 속도선 양쪽 페이드(양 끝 모두 투명). 0/미지정(구버전)이면 한쪽만 페이드.
    public bool LineFadeBothSides { get; set; }
    // 모서리 조절(사변형 일그러뜨림) 변위 TL,TR,BR,BL × X,Y = 8개. 0이면 일그러짐 없음.
    public double[] CornerOffsets { get; set; } = new double[8];
    // 모서리 조절을 도형/글자에 적용할지(개별).
    public bool WarpShape { get; set; }
    public bool WarpText { get; set; }
    // 테두리 없음 말풍선의 글자 회전 각도(도). 0/미지정이면 회전 없음.
    public double TextRotation { get; set; }
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
    // 생각 말풍선 꼬리(원 3개). 0/미지정(구버전)이면 일반 곡선 꼬리.
    public bool ThoughtTail { get; set; }
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
    public bool PageWidthFit { get; set; }
    public string LayoutPattern { get; set; } = "1,2,1";
    public string AutoMargin { get; set; } = "24";
    public string AutoGutter { get; set; } = "14";
    public string BubbleShape { get; set; } = "Oval";
    public bool InspectorVisible { get; set; } = true;
    // 선택 미리보기 강조(마우스를 올린 곳에서 클릭 시 선택될 오브젝트를 미리 강조). 기본 OFF.
    public bool SelectionPreview { get; set; }
    // 이미지 크기 조절 시 항상 비율 유지. OFF면 자유 리사이즈(Shift로 일시 비율 유지). 기본 ON.
    public bool KeepAspectRatio { get; set; } = true;
    // 자동저장 끄기. ON이면 편집 중 주기적 자동저장을 멈춘다(렉 완화). 기본 OFF(자동저장 켜짐).
    public bool AutosaveDisabled { get; set; }
    // 이미지 디코드 캐시 한도(MB). 같은 파일 재디코드를 피해 페이지 전환을 빠르게 한다. 0이면 캐시 끔. 기본 256.
    public int ImageCacheLimitMb { get; set; } = 256;
    // 사용자 지정 단축키(액션 id → "Ctrl+S" 같은 표기). 없는 항목은 기본값을 쓴다.
    public Dictionary<string, string>? Shortcuts { get; set; }
    // 색 선택기에서 고른 최근 임의 색(최신순, hex).
    public List<string>? RecentColors { get; set; }
    // 마지막 내보내기 설정(다음 실행에도 기억). ExportWebp=null이면 형식은 페이지 움직임 유무로 자동 선택.
    public double ExportScale { get; set; } = 1;
    public bool? ExportWebp { get; set; }
    public bool ExportLossless { get; set; } = true;
    public int ExportQuality { get; set; } = 90;
}
