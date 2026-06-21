using Microsoft.Win32;
using System.Collections.ObjectModel;
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
    private double _pageWidth = 832;
    private double _pageHeight = 1216;
    private double _inspectorWidth = 360; // 드래그로 조절한 인스펙터 폭(토글 복원용).
    // 마우스 호버 시 '클릭하면 선택될 대상'을 보여주는 작은 툴팁.
    private System.Windows.Controls.Primitives.Popup? _hoverPopup;
    private TextBlock? _hoverText;
    // 실행 취소/다시 실행: 문서 상태를 '페이지별 JSON 목록' 스냅샷으로 보관한다.
    // 한 동작은 현재 페이지 한 장만 바꾸므로, 스냅샷마다 바뀐 페이지만 새로 직렬화하고
    // 안 바뀐 페이지는 이전 스냅샷의 문자열을 '공유'한다 → 페이지 수와 무관한 캡처 비용·메모리.
    private readonly List<HistorySnapshot> _undoStack = new();
    private readonly List<HistorySnapshot> _redoStack = new();
    private HistorySnapshot? _baseline;
    // 변화 감지용 경량 서명(제목+현재페이지+페이지수+이름목록+현재페이지JSON). 클릭/선택 등 무변경 입력에서
    // 전체 직렬화를 건너뛰기 위해, 매 틱엔 이 싼 서명만 비교한다(편집은 현재 페이지에서만 일어남).
    private string _lastChangeSignature = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _historyTimer;
    private const int MaxHistory = 60;
    // 입력(마우스·키)이 있을 때만 true가 되어, idle 상태에서 전체 문서를 매 틱 직렬화하는 것을 막는다.
    private bool _historyDirty;
    // 페이지 추가/삭제/순서이동처럼 '여러 페이지의 위치'가 바뀐 직후엔 인덱스 기반 재사용이 어긋나므로,
    // 다음 캡처에서 전체 페이지를 다시 직렬화하도록 강제하는 플래그.
    private bool _historyStructuralPending;

    // 자동저장 디바운스: undo 기록은 즉시(메모리) 남기되, '디스크 쓰기 + 전체 JSON 조립'은
    // 편집이 잠시 멈춘 뒤(유휴) 한 번만 한다. 연속 편집 중에도 상한 간격마다 강제 저장해 크래시 손실을 막는다.
    private bool _autosavePending;
    private int _autosaveIdleTicks; // 마지막 변경 이후 지난 틱(유휴 판정).
    private int _autosaveAgeTicks;  // 미저장 상태가 된 뒤 지난 틱(강제 저장 상한).
    private const int AutosaveFlushIdleTicks = 2;  // 약 1.2초 동안 변경이 없으면 저장.
    private const int AutosaveFlushMaxTicks = 10;  // 연속 편집이라도 약 6초마다 강제 저장.

    // 한 시점의 문서 상태. PageJsons는 페이지별 JSON(불변 문자열)이며, 안 바뀐 페이지는
    // 여러 스냅샷이 같은 문자열 인스턴스를 공유하므로 실제 메모리는 '바뀐 페이지'만큼만 늘어난다.
    private sealed class HistorySnapshot
    {
        public string Title = string.Empty;
        public int CurrentIndex;
        public List<string> PageJsons = new();
        // 프로젝트 전체 본문 텍스트(노벨 뷰어)의 직렬화 JSON. 페이지와 무관한 단일 객체.
        public string FlowJson = "{}";
        // 비주얼 노벨 템플릿 목록 JSON과, 그 시점에 편집 중이던 템플릿 인덱스(-1이면 일반 페이지 편집).
        public string VnTemplatesJson = "[]";
        public int VnEditingIndex = -1;
    }

    private readonly List<ComicPanel> _panels = new();
    // 한 번에 하나만 선택된다(칸/이미지/말풍선). _selectedPanel은 이미지·말풍선 선택 시
    // 리스트/인스펙터의 맥락(소속 칸)으로도 쓰이며, 실제로 '활성 선택'이 무엇인지는 _selectionKind가 가린다.
    private SelectionKind _selectionKind = SelectionKind.None;
    private ComicPanel? _selectedPanel;
    private SpeechBubble? _selectedBubble;
    private BubbleTail? _selectedBubbleTail;
    private PanelImage? _selectedImage;
    private bool _isUpdatingPanelList;
    private Point _dragStart;
    private Point _imageDragStart;
    // 이미지 드래그 시작 시점의 Translate(절대 위치 계산용 — 스냅 누적 드리프트 방지).
    private Point _imageDragOrigin;
    private bool _isDraggingPanel;
    private bool _isDraggingPanelImage;
    private bool _isDraggingBubble;
    // 마우스 다운 시점에 선택하지 않고, (드래그 없이) 업할 때 선택할 대상(칸/이미지/말풍선). 드래그·휠은 이미 선택된 것만.
    private object? _pendingSelect;
    // 이미 선택된(겹친) 오브젝트를 다시 클릭했을 때, (드래그 없이) 업하면 선택할 '한 단계 안쪽' 대상.
    private object? _pendingCycle;
    // 보류 선택의 다운 위치(PageOverlay 좌표). 업 전에 이 거리 이상 움직이면 클릭이 아니라고 보고 선택을 취소한다.
    private Point _pendingDownPos;
    private const double PendingMoveCancelThreshold = 6;
    private bool _isLoadingInspector;
    // 생성자 완료 전(특히 XAML 파싱 중 초기 Text 설정으로 TextChanged가 일찍 발화) 본문 텍스트 핸들러가
    // 아직 만들어지지 않은 컨트롤을 건드려 NRE가 나는 것을 막는다. 생성자 끝에서 true.
    private bool _flowReady;
    private bool _isUpdatingBubbleList;
    private bool _isUpdatingBubbleTailList;
    private bool _isUpdatingImageList;
    private bool _isUpdatingPageList;
    private int _nextPanelNumber = 1;
    // 페이지 목록(PageListBox.ItemsSource로 바인딩). ObservableCollection이라 추가/삭제/이동이
    // 목록 전체 재구성 없이 '바뀐 항목만' 증분 갱신된다(스크롤·선택 보존, 페이지 수와 무관한 비용).
    private readonly ObservableCollection<ComicPageData> _pages = new();
    // 비주얼 노벨 생성용 템플릿 페이지(일반 페이지와 별개, VN 섹션 목록에 바인딩).
    private readonly ObservableCollection<ComicPageData> _vnTemplates = new();
    // null이면 일반 페이지를 편집 중, 아니면 이 VN 템플릿을 캔버스에서 편집 중(SaveCurrentPageState가 여기로 저장).
    private ComicPageData? _editingTemplate;
    // 일반 페이지 목록 ↔ VN 템플릿 목록 선택 동기화 중 재진입 방지.
    private bool _isSwitchingEditTarget;
    private int _currentPageIndex;
    private string? _projectBaseDirectory;
    // 현재 불러왔거나 저장한 프로젝트 파일 전체 경로(Ctrl+S 덮어쓰기 대상). 없으면 다른 이름으로 저장.
    private string? _projectFilePath;
    // 꼬리 편집용 세 점 핸들은 칸 경계 클리핑을 피하기 위해 페이지 레이어(PageOverlay)에
    // 올려 두는 싱글톤이다. 선택된 말풍선의 선택된 꼬리에만 표시된다.
    private Thumb? _tailStartHandle;
    private Thumb? _tailMidHandle;
    private Thumb? _tailEndHandle;
    // 말풍선 선택 박스도 칸 경계에 잘리지 않도록 PageOverlay에 싱글톤으로 둔다.
    // (8방향 리사이즈 핸들은 MainWindow.Resize.cs의 _bubbleResizeHandles가 담당한다.)
    private Border? _bubbleSelectionBox;
    // 칸 선택 박스도 칸 경계에 잘리지 않도록 PageOverlay에 싱글톤으로 둔다(칸 자체 테두리와 별개).
    private Border? _panelSelectionBox;
    // 이미지 선택 테두리 박스(핸들 없음). PageOverlay 싱글톤이라 칸 밖으로 나가도 안 잘린다.
    private Border? _imageSelectionBox;
    // 이동 시 스냅 가이드 선(세로=X 스냅, 가로=Y 스냅). PageOverlay 싱글톤.
    private System.Windows.Shapes.Line? _snapGuideX;
    private System.Windows.Shapes.Line? _snapGuideY;
    // 호버 강조: 커서를 올렸을 때 '클릭하면 선택될' 오브젝트를 미리 강조한다.
    private Border? _hoverBox;          // 호버 강조용 싱글톤 박스(말풍선·이미지 공용, PageOverlay).
    private object? _hoveredObject;     // 현재 호버 강조 중인 오브젝트(칸/이미지/말풍선).
    private bool _selectionPreviewEnabled; // '선택 미리보기 강조' ON/OFF(환경설정-일반). 기본 OFF.
    private bool _keepAspectRatio = true;   // '이미지 크기 조절 시 비율 유지' ON/OFF(환경설정-일반). 기본 ON. 이미지에만 적용.
    private bool _autosaveDisabled;         // '자동저장 끄기' ON/OFF(환경설정-일반). 기본 OFF(자동저장 켜짐). 켜면 편집 중 주기적 자동저장을 멈춰 렉을 줄인다.
    private double _resizeStartAspect = 1;  // 리사이즈 시작 시점의 가로/세로 비율(비율 유지 기준).
    // 텍스트 영역(여백)을 직접 조절하는 4모서리 핸들(싱글톤, PageOverlay).
    private Thumb? _textRegionTopLeft;
    private Thumb? _textRegionTopRight;
    private Thumb? _textRegionBottomLeft;
    private Thumb? _textRegionBottomRight;
    // 칸 사변형 모서리 조절 핸들(싱글톤, PageOverlay). 인덱스 0=TL,1=TR,2=BR,3=BL.
    private Thumb[]? _panelCornerHandles;
    // 앱 설정(창 크기·단축키 등)은 사용자 폴더(%AppData%\KomaForge)에 저장한다.
    private readonly string _windowSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KomaForge",
        "komaforge-settings.json");
    // 구버전 설정 파일명(없으면 무시). 새 이름이 없을 때 한 번 이 파일에서 불러온다.
    private readonly string _legacyWindowSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KomaForge",
        "window-settings.json");
    // 작업 내용 자동 저장(다음 실행 시 자동 복원). 명시적 저장/불러오기와는 별개.
    // 이미지 상대 경로 포터블성을 위해 실행 파일과 같은 폴더에 둔다(설정과 다름).
    private readonly string _autosavePath = Path.Combine(
        AppContext.BaseDirectory,
        "autosave.kfjson");
    // 옛 이름(NovelViewer 시절)의 자동 저장본 — 새 파일이 없을 때만 복원에 사용.
    private readonly string _legacyAutosavePath = Path.Combine(
        AppContext.BaseDirectory,
        "autosave.nvjson");

    public MainWindow()
    {
        InitializeComponent();
        InitShortcuts();          // 기본 단축키 채우기(LoadWindowSettings에서 사용자 지정으로 덮어쓸 수 있음)
        LoadWindowSettings();
        RefreshShortcutMenuText(); // 메뉴의 단축키 표기를 현재 설정으로 맞춘다
        InitColorCombos(); // 팔레트 + 최근색 + '직접 지정…' 으로 색 콤보 구성(LoadWindowSettings 이후라 최근색 반영).
        InitBubbleFontCombo(); // 시스템 글꼴 목록으로 말풍선 글꼴 콤보 구성.
        VnTemplateListBox.ItemsSource = _vnTemplates; // 비주얼 노벨 템플릿 목록 바인딩.
        _flowReady = true;     // 모든 컨트롤이 만들어졌으므로 본문 텍스트 핸들러를 활성화.
        Closing += (_, _) =>
        {
            SaveWindowSettings();
            AutoSave(CaptureSnapshot());
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        // 문서를 바꿀 수 있는 모든 상호작용은 마우스/키 입력을 동반하므로, 입력이 있을 때만
        // 히스토리 타이머가 스냅샷을 뜨도록 dirty 표시한다(idle 시 불필요한 전체 직렬화 방지).
        PreviewMouseDown += (_, _) => _historyDirty = true;
        PreviewMouseWheel += (_, _) => _historyDirty = true;
        // 저장된 기본 칸 구성(없으면 입력칸 기본값)으로 첫 페이지를 생성한다.
        CreateLayoutFromPattern(LayoutPatternTextBox.Text);
        _pages.Add(CaptureCurrentPage("Page 1"));
        // 이전 작업(자동 저장본)이 있으면 기본 페이지 대신 그것을 불러온다.
        TryLoadAutosave();
        UpdatePageList();
        UpdateInspectorLabels();
        UpdateWindowTitle(); // 시작 시 제목을 버전 포함으로 세팅(XAML 기본 제목 대체).

        // 히스토리 초기화: 현재 상태를 기준선으로 삼고, 변화 감지 타이머를 돌린다.
        ResetHistoryBaseline();
        UpdateUndoRedoButtons();
        _historyTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _historyTimer.Tick += (_, _) => HistoryTick();
        _historyTimer.Start();

        // 창 레이아웃이 끝난 뒤 기준선을 다시 잡아 시작 시 생기는 잡음 히스토리를 제거한다.
        Loaded += (_, _) =>
        {
            ResetHistoryBaseline();
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateUndoRedoButtons();

            // 긴 페이지 성능: 스크롤/크기 변경 시 뷰포트 밖 칸을 컬링한다.
            PageScrollViewer.ScrollChanged += (_, _) => CullOffscreenPanels();
            PageScrollViewer.SizeChanged += (_, _) => CullOffscreenPanels();
            CullOffscreenPanels();
        };
    }

    // Ctrl 키를 누르거나 떼면, 마우스를 움직이지 않아도 호버 툴팁(클릭/Ctrl+클릭 대상)을 즉시 갱신한다.
    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            UpdateHoverTooltip(Mouse.DirectlyOver as DependencyObject);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _historyDirty = true; // 키 입력(텍스트 편집·단축키 등)은 변경 가능성으로 본다.

        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        {
            UpdateHoverTooltip(Mouse.DirectlyOver as DependencyObject);
        }

        // 사용자 지정 가능한 명령 단축키(불러오기/저장/실행취소·다시실행/잘라내기·복사·붙여넣기/리셋/잠금/비주얼 노벨 모드).
        if (TryRunCustomShortcut(e))
        {
            e.Handled = true;
            return;
        }

        // F2: 페이지 섹션에 포커스가 있고 선택된 페이지가 있으면 이름 인라인 편집 시작.
        if (e.Key == Key.F2)
        {
            if (Keyboard.FocusedElement is not TextBox &&
                PageSectionBorder != null && PageSectionBorder.IsKeyboardFocusWithin &&
                PageListBox.SelectedIndex >= 0)
            {
                StartPageRename(PageListBox.SelectedIndex);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            // 페이지/칸 이름 편집 중이면 그 입력칸의 취소(KeyDown)에 맡긴다(선택 해제하지 않음).
            if (Keyboard.FocusedElement is TextBox &&
                (_pages.Any(p => p.IsEditing) || _panels.Any(p => p.IsEditing)))
            {
                return;
            }

            ClearSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            // 텍스트 입력 중에는 Delete가 글자 삭제이므로 오브젝트를 삭제하지 않는다.
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (DeleteSelectedBubble() || DeleteSelectedImage())
            {
                e.Handled = true;
            }
            return;
        }

        // PgUp / PgDn: 인스펙터·선택과 무관하게 페이지 넘김(텍스트 입력 중엔 제외).
        if (e.Key == Key.PageUp || e.Key == Key.PageDown)
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            NavigatePage(e.Key == Key.PageUp ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            // 인스펙터가 닫혀 있으면 위/아래 키로 페이지를 넘긴다(선택 상태와 무관).
            // (오브젝트 순서 이동은 인스펙터의 위로/아래로 버튼으로만 한다 — 키보드 단축키는 제거.)
            if (!IsInspectorOpen())
            {
                NavigatePage(e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
            }
        }
    }

    private bool IsInspectorOpen() => InspectorPanel.Visibility == Visibility.Visible;

    // 페이지 섹션(리스트·옵션) 안에 키보드 포커스가 있을 때만 하위 옵션을 보이고,
    // 다른 섹션(칸/이미지/말풍선)과 동일하게 배경색도 강조한다.
    private void PageSection_FocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PageSectionBorder == null)
        {
            return;
        }

        var focused = PageSectionBorder.IsKeyboardFocusWithin;

        // 페이지에 포커스가 들어오면 오브젝트 선택을 해제해, 칸+페이지가 동시에 선택돼 보이는 일을 막는다.
        if (focused && _selectionKind != SelectionKind.None)
        {
            ClearSelection(announce: false);
        }

        SetPageSelected(focused);
    }

    // 페이지 섹션의 강조(배경색)와 하위 옵션 표시를 함께 토글한다.
    private void SetPageSelected(bool selected)
    {
        if (PageEditControls == null || PageSectionBorder == null)
        {
            return;
        }

        PageEditControls.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SetSectionHighlight(PageSectionBorder, selected);
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        CreateLayoutFromPattern(LayoutPatternTextBox.Text);
    }

    private void PageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 템플릿 편집 중이면 같은 인덱스를 골라도 일반 페이지 편집으로 복귀해야 하므로 인덱스 동일 가드는 그때 건너뛴다.
        if (_isUpdatingPageList || PageListBox.SelectedIndex < 0
            || (_editingTemplate == null && PageListBox.SelectedIndex == _currentPageIndex))
        {
            return;
        }

        // 마우스 클릭으로 전환했는지(키보드 페이지 넘김과 구분). 클릭이면 전환 뒤 포커스를 리스트로 되돌린다.
        var fromClick = Mouse.LeftButton == MouseButtonState.Pressed
            || (PageSectionBorder != null && PageSectionBorder.IsKeyboardFocusWithin);

        SaveCurrentPageState();          // 직전 편집 대상(일반 페이지 또는 템플릿) 저장.
        _editingTemplate = null;         // 일반 페이지 편집으로 복귀.
        _isSwitchingEditTarget = true;
        VnTemplateListBox.SelectedIndex = -1; // VN 템플릿 선택 해제.
        _isSwitchingEditTarget = false;
        _currentPageIndex = PageListBox.SelectedIndex;
        LoadPage(_pages[_currentPageIndex]);
        UpdateStatus($"{_pages[_currentPageIndex].Name} 페이지를 열었습니다.");

        if (fromClick)
        {
            // 전환 중 포커스가 캔버스 등으로 옮겨가 페이지 하위 옵션이 사라지는 것을 막는다(컬링 이후에 실행).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PageSectionBorder != null && !PageSectionBorder.IsKeyboardFocusWithin)
                {
                    PageListBox.Focus();
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    // --- 페이지 이름 인라인 편집(더블클릭 / F2) ---

    private string _pageNameBeforeEdit = "";

    private void PageListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        StartPageRename(PageListBox.SelectedIndex);
    }

    private void PageListBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F2)
        {
            StartPageRename(PageListBox.SelectedIndex);
            e.Handled = true;
        }
    }

    private void StartPageRename(int index)
    {
        if (index < 0 || index >= _pages.Count)
        {
            return;
        }

        foreach (var p in _pages)
        {
            p.IsEditing = false;
        }

        _pageNameBeforeEdit = _pages[index].Name;
        _pages[index].IsEditing = true; // DataTrigger가 입력칸을 표시 → IsVisibleChanged에서 포커스.
    }

    private void PageRenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
        {
            // 모든 클릭/선택 처리가 끝난 뒤(Background) 포커스를 잡아, 리스트가 포커스를 도로 가져가
            // 즉시 LostFocus로 닫히는 것을 막는다.
            tb.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.SelectAll();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void PageRenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ComicPageData page)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitPageRename(page);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            page.Name = _pageNameBeforeEdit; // 되돌리기
            page.IsEditing = false;
            e.Handled = true;
        }
    }

    private void PageRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ComicPageData page && page.IsEditing)
        {
            CommitPageRename(page);
        }
    }

    private void CommitPageRename(ComicPageData page)
    {
        if (string.IsNullOrWhiteSpace(page.Name))
        {
            page.Name = string.IsNullOrWhiteSpace(_pageNameBeforeEdit) ? "Page" : _pageNameBeforeEdit;
        }

        page.IsEditing = false;
        _historyDirty = true; // 이름 변경은 문서 변경으로 기록.
    }

    // --- 칸 이름 인라인 편집(더블클릭 / F2). 빈 이름으로 확정하면 기본 "N번 칸" 표시로 돌아간다. ---

    private string _panelNameBeforeEdit = "";

    private void PanelListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        StartPanelRename(PanelListBox.SelectedItem as ComicPanel);
    }

    private void PanelListBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F2)
        {
            StartPanelRename(PanelListBox.SelectedItem as ComicPanel);
            e.Handled = true;
        }
    }

    private void StartPanelRename(ComicPanel? panel)
    {
        if (panel == null)
        {
            return;
        }

        foreach (var p in _panels)
        {
            p.IsEditing = false;
        }

        _panelNameBeforeEdit = panel.Name;
        panel.IsEditing = true; // DataTrigger가 입력칸을 표시 → IsVisibleChanged(공용)에서 포커스.
    }

    private void PanelRenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ComicPanel panel)
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitPanelRename(panel);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            panel.Name = _panelNameBeforeEdit; // 되돌리기
            panel.IsEditing = false;
            e.Handled = true;
        }
    }

    private void PanelRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ComicPanel panel && panel.IsEditing)
        {
            CommitPanelRename(panel);
        }
    }

    private void CommitPanelRename(ComicPanel panel)
    {
        // 빈 이름은 그대로 허용(기본 "N번 칸" 표시로 복귀).
        panel.Name = panel.Name?.Trim() ?? string.Empty;
        panel.IsEditing = false;
        _historyDirty = true;
    }

    // 추가: 현재 페이지 아래에 기본 칸 구성의 새 페이지를 만든다(현재 페이지 크기만 따름, 내용 복사 없음).
    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        LeaveTemplateEditing(); // 템플릿 편집 중이었다면 일반 페이지 편집으로 복귀 후 진행.
        SaveCurrentPageState();
        _historyStructuralPending = true; // 페이지 추가: 다음 캡처에서 전체 재직렬화.

        var insertAt = (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count) ? _currentPageIndex + 1 : _pages.Count;
        _pages.Insert(insertAt, new ComicPageData { Name = $"Page {_pages.Count + 1}", PageWidth = _pageWidth, PageHeight = _pageHeight });
        _currentPageIndex = insertAt;
        LoadPage(_pages[_currentPageIndex]);
        CreateLayoutFromPattern(LayoutPatternTextBox.Text); // 기본 칸 구성으로 채운다(패턴이 비어 있으면 빈 페이지).
        ClearSelection();
        UpdatePageList();
        UpdateStatus("새 페이지를 추가했습니다.");
    }

    // 복제: 현재 페이지를 바로 아래에 통째로 복제한다(크기·배경·칸·내용 등 모든 설정).
    private void DuplicatePage_Click(object sender, RoutedEventArgs e)
    {
        LeaveTemplateEditing(); // 템플릿 편집 중이었다면 일반 페이지 편집으로 복귀 후 진행.
        if (_currentPageIndex < 0 || _currentPageIndex >= _pages.Count)
        {
            AddPage_Click(sender, e); // 복제할 현재 페이지가 없으면 단순 추가.
            return;
        }

        SaveCurrentPageState();
        _historyStructuralPending = true;

        // JSON 왕복으로 깊은 복사해 페이지끼리 데이터를 공유하지 않게 한다.
        var src = _pages[_currentPageIndex];
        var clone = JsonSerializer.Deserialize<ComicPageData>(JsonSerializer.Serialize(src))!;
        clone.Name = $"Page {_pages.Count + 1}"; // 순번만 늘린다('복사' 누적 방지).
        var insertAt = _currentPageIndex + 1;
        _pages.Insert(insertAt, clone);
        _currentPageIndex = insertAt;
        LoadPage(_pages[_currentPageIndex]);
        ClearSelection();
        UpdatePageList();
        UpdateStatus("현재 페이지를 아래에 복제했습니다.");
    }

    private void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        LeaveTemplateEditing(); // 템플릿 편집 중이었다면 일반 페이지 편집으로 복귀 후 진행.
        if (_pages.Count <= 1)
        {
            UpdateStatus("페이지는 최소 1개가 필요합니다.");
            return;
        }

        _historyStructuralPending = true; // 페이지 삭제: 다음 캡처에서 전체 재직렬화.
        _pages.RemoveAt(_currentPageIndex);
        _currentPageIndex = Math.Clamp(_currentPageIndex, 0, _pages.Count - 1);
        LoadPage(_pages[_currentPageIndex]);
        UpdatePageList();
        UpdateStatus("페이지를 삭제했습니다.");
    }

    private void MovePageUp_Click(object sender, RoutedEventArgs e)
    {
        MoveCurrentPage(-1);
    }

    private void MovePageDown_Click(object sender, RoutedEventArgs e)
    {
        MoveCurrentPage(1);
    }

    private void MoveCurrentPage(int direction)
    {
        LeaveTemplateEditing(); // 템플릿 편집 중이었다면 일반 페이지 편집으로 복귀 후 진행.
        var target = _currentPageIndex + direction;
        if (_currentPageIndex < 0 || target < 0 || target >= _pages.Count)
        {
            return;
        }

        // 현재 편집 내용을 페이지 데이터에 반영한 뒤 위치만 옮긴다(표시 중인 페이지는 그대로).
        SaveCurrentPageState();
        _historyStructuralPending = true; // 페이지 순서 이동: 다음 캡처에서 전체 재직렬화.
        // 인접 이동이라 Move 한 번으로 교환과 동일. 목록은 해당 항목만 증분 이동된다.
        // Move가 부르는 SelectionChanged가 '페이지 전환'으로 오해되지 않게 가드로 감싼다.
        _isUpdatingPageList = true;
        _pages.Move(_currentPageIndex, target);
        _currentPageIndex = target;
        _isUpdatingPageList = false;
        UpdatePageList();
        UpdateStatus("페이지 순서를 옮겼습니다.");
    }

    private void PageSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector ||
            PageWidthTextBox == null ||
            PageHeightTextBox == null ||
            PageSurface == null ||
            !double.TryParse(PageWidthTextBox.Text, out var width) ||
            !double.TryParse(PageHeightTextBox.Text, out var height))
        {
            return;
        }

        // 페이지 크기는 페이지마다 개별 적용된다. 입력 즉시 현재 페이지에 반영한다.
        SetPageSize(width, height, false);
        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
        {
            _pages[_currentPageIndex].PageWidth = _pageWidth;
            _pages[_currentPageIndex].PageHeight = _pageHeight;
        }
    }

    private void SetPageSize(double width, double height, bool updateTextBoxes)
    {
        var newW = Math.Clamp(width, 100, 5000);
        var newH = Math.Clamp(height, 100, 5000);
        _pageWidth = newW;
        _pageHeight = newH;

        PageSurface.Width = _pageWidth;
        PageSurface.Height = _pageHeight;
        PanelCanvas.Width = _pageWidth;
        PanelCanvas.Height = _pageHeight;
        PageOverlay.Width = _pageWidth;
        PageOverlay.Height = _pageHeight;

        if (updateTextBoxes)
        {
            _isLoadingInspector = true;
            PageWidthTextBox.Text = $"{_pageWidth:0}";
            PageHeightTextBox.Text = $"{_pageHeight:0}";
            _isLoadingInspector = false;
        }

        UpdatePageFit();
    }

    private bool _updatingFitMenu;

    // 쪽 맞춤 / 폭 맞춤은 라디오처럼 상호 배타(둘 중 하나만 켜짐).
    private void PageFitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFitMenu)
        {
            return;
        }

        _updatingFitMenu = true;
        if (ReferenceEquals(sender, PageFitMenuItem) && PageFitMenuItem.IsChecked == true)
        {
            PageWidthFitMenuItem.IsChecked = false;
        }
        else if (ReferenceEquals(sender, PageWidthFitMenuItem) && PageWidthFitMenuItem.IsChecked == true)
        {
            PageFitMenuItem.IsChecked = false;
        }
        _updatingFitMenu = false;

        UpdatePageFit();

        // 보기 설정은 토글 즉시 저장한다(강제 종료·크래시로 닫혀도 유실되지 않도록).
        // 단, 불러오기 도중 메뉴를 세팅할 때는 저장하지 않는다(절반만 로드된 상태 저장 방지).
        if (!_loadingWindowSettings)
        {
            SaveWindowSettings();
        }
    }

    private void PageBackgroundColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnColorComboChanged(PageBackgroundColorComboBox, true,
            () => CurrentPageBackgroundColor(),
            _ => ApplyPageBackground(), // 콤보 선택이 최신이면 그 색을 읽어 적용.
            Colors.White);
    }

    // 현재 페이지 배경색(콤보 선택값, 없으면 흰색).
    private Color CurrentPageBackgroundColor() => GetComboColor(PageBackgroundColorComboBox, Colors.White);

    // 페이지 배경을 선택한 색으로 적용한다(내보내기 결과 배경에도 반영됨).
    private void ApplyPageBackground()
    {
        var brush = new SolidColorBrush(CurrentPageBackgroundColor());
        if (PageSurface != null)
        {
            PageSurface.Background = brush;
        }
        if (PageFrame != null)
        {
            PageFrame.Background = brush; // 페이지 뒤 레이어도 페이지 색으로(1px 테두리 오프셋 메움).
        }
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        SetInspectorVisible(InspectorPanel.Visibility != Visibility.Visible);
    }

    private void SetInspectorVisible(bool show)
    {
        // 숨기기 전 현재(드래그로 조절된) 폭을 기억해 둔다.
        if (!show && InspectorColumn.Width.Value > 0)
        {
            _inspectorWidth = InspectorColumn.Width.Value;
        }

        InspectorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = show ? new GridLength(_inspectorWidth) : new GridLength(0);
        InspectorToggleButton.Content = show ? "◀" : "▶";

        // 인스펙터를 닫으면 뷰어 모드: 모든 선택을 해제한다(닫힌 동안 선택 불가).
        if (!show)
        {
            ClearSelection();
        }
        // 인스펙터를 접으면 페이지 영역 너비가 바뀌므로 쪽 맞춤을 다시 계산한다
        // (ScrollViewer SizeChanged로도 갱신되지만 즉시 반영을 위해 호출).
        UpdatePageFit();
    }

    private void PageScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 선택된 이미지가 칸 밖으로 벗어난 경우: 그 벗어난 부분(칸 프레임 밖)을 눌러도 드래그로 이동할 수 있게 한다.
        // (칸 프레임 밖이라 프레임의 다운 핸들러가 닿지 않으므로 여기서 가로채 처리한다.)
        if (ShouldBeginOverflowImageDrag(e))
        {
            BeginOverflowImageDrag(e);
            e.Handled = true;
            return;
        }

        // 페이지(PageSurface) 밖 — 스크롤뷰어 여백 — 을 클릭하면 선택 해제.
        if (!IsInspectorOpen()) return; // 뷰어 모드에서는 어차피 선택이 없다.
        var node = e.OriginalSource as DependencyObject;
        while (node != null)
        {
            if (ReferenceEquals(node, PageSurface)) return; // 페이지 안 → 페이지 핸들러가 처리.
            node = VisualTreeHelper.GetParent(node);
        }
        ClearSelection();
    }

    private void PageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 인스펙터가 닫혀 있으면 휠은 항상 페이지 넘김(선택/위치와 무관).
        if (!IsInspectorOpen())
        {
            NavigatePage(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        // 이미지가 선택돼 있으면 커서 위치와 무관하게(이미지 위에 올릴 필요 없이) 휠로 이미지 크기를 조절한다.
        if (_selectionKind == SelectionKind.Image && _selectedImage != null)
        {
            ZoomImage(_selectedImage, e);
            return;
        }

        // 편집 모드라도 커서가 페이지 바깥(여백)에 있으면 휠로 페이지를 넘긴다.
        if (PageSurface != null && !IsMouseOverElement(PageSurface, e))
        {
            NavigatePage(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        // 휠 크기조절은 '이미 선택된' 오브젝트에만(먼저 선택 → 휠로 크기 조절).
        // 커서가 선택 오브젝트의 '영역' 위에 있으면, 앞에 다른 오브젝트가 가리고 있어도 적용한다(기하 판정).

        // 선택된 말풍선 영역 위에서 휠 → 말풍선 크기.
        if (_selectionKind == SelectionKind.Bubble && _selectedBubble != null &&
            IsMouseOverElement(_selectedBubble.Container, e))
        {
            ZoomBubble(_selectedBubble, e);
            return;
        }

        // 그 외(선택 안 됨 등): 윈도우 휠 설정에 맞춰 세로 스크롤(WPF 기본은 설정을 무시하므로 직접 처리).
        if (PageScrollViewer.ScrollableHeight > 0)
        {
            PageScrollViewer.ScrollToVerticalOffset(
                PageScrollViewer.VerticalOffset - WheelScrollPixels(PageScrollViewer, e.Delta));
            e.Handled = true;
        }
    }

    // 커서가 해당 요소의 사각 영역(로컬 0,0~크기) 위에 있는지(가려져 있어도 기하만 판정).
    private static bool IsMouseOverElement(FrameworkElement element, MouseEventArgs e)
    {
        var p = e.GetPosition(element);
        var w = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
        var h = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
        return p.X >= 0 && p.Y >= 0 && p.X <= w && p.Y <= h;
    }

    // 마우스 위치의 칸을 비주얼 트리에서 거슬러 올라가 찾는다.
    private ComicPanel? FindPanelAt(object? source)
    {
        var node = source as DependencyObject;
        while (node != null)
        {
            if (node is Border border)
            {
                var match = _panels.FirstOrDefault(p => ReferenceEquals(p.Frame, border));
                if (match != null)
                {
                    return match;
                }
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : null;
        }

        return null;
    }

    // 현재 페이지에서 direction(-1 이전 / +1 다음)만큼 페이지를 전환한다.
    private void NavigatePage(int direction)
    {
        var target = _currentPageIndex + direction;
        if (target < 0 || target >= _pages.Count)
        {
            return;
        }

        // 페이지 목록 선택을 바꾸면 PageListBox_SelectionChanged가 저장/로드/상태표시를 처리한다.
        PageListBox.SelectedIndex = target;
    }

    // 윈도우의 '휠 스크롤 줄 수' 설정(SystemParameters.WheelScrollLines)에 맞춘 세로 스크롤 픽셀량.
    // -1(한 번에 한 화면)이면 뷰포트 높이만큼. 줄당 16px(윈도우 기본 3줄 = 48px, WPF 기본과 동일).
    private static double WheelScrollPixels(ScrollViewer sv, int wheelDelta)
    {
        var lines = SystemParameters.WheelScrollLines;
        return lines < 0
            ? sv.ViewportHeight * (wheelDelta / 120.0)
            : lines * 16.0 * (wheelDelta / 120.0);
    }

    // 인스펙터 안의 리스트 등이 휠을 먼저 소비해 바깥이 안 스크롤되는 문제를 막기 위해,
    // 터널링 단계에서 인스펙터 ScrollViewer를 직접 스크롤한다(윈도우 휠 설정 반영).
    private void InspectorScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 커서 아래에 자체 스크롤 가능한 내부 스크롤뷰어(리스트/여러 줄 텍스트박스)가 있고 그 방향으로 더
        // 스크롤할 수 있으면, 그 컨트롤을 '한 번에 최대 한 화면'으로 제한해 직접 스크롤한다(휠이 너무 많이 넘어가지 않게).
        var inner = FindScrollableInner(e.OriginalSource as DependencyObject, e.Delta);
        if (inner != null)
        {
            ScrollViewerByNotch(inner, e.Delta);
            e.Handled = true;
            return;
        }

        InspectorScrollViewer.ScrollToVerticalOffset(
            InspectorScrollViewer.VerticalOffset - WheelScrollPixels(InspectorScrollViewer, e.Delta));
        e.Handled = true;
    }

    // OriginalSource에서 InspectorScrollViewer까지 거슬러 올라가며, 휠 방향으로 더 스크롤 가능한
    // 내부 스크롤뷰어(리스트·텍스트박스의 내부 ScrollViewer)를 찾는다.
    private ScrollViewer? FindScrollableInner(DependencyObject? src, int delta)
    {
        while (src != null && !ReferenceEquals(src, InspectorScrollViewer))
        {
            if (src is ScrollViewer sv && !ReferenceEquals(sv, InspectorScrollViewer)
                && sv.ScrollableHeight > 0.5
                && (delta > 0 ? sv.VerticalOffset > 0.5 : sv.VerticalOffset < sv.ScrollableHeight - 0.5))
            {
                return sv;
            }
            src = src is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(src) : null;
        }
        return null;
    }

    // 휠 한 칸당 스크롤량을 '한 화면(보이는 만큼)' 이하로 제한해 스크롤한다.
    private static void ScrollViewerByNotch(ScrollViewer sv, int delta)
    {
        var notches = delta / 120.0;
        var lines = SystemParameters.WheelScrollLines; // -1이면 '한 번에 한 화면'.
        // 아이템 단위(CanContentScroll)면 ViewportHeight=아이템 수, 픽셀 단위면 줄당 약 16px.
        var perNotch = lines < 0
            ? sv.ViewportHeight
            : (sv.CanContentScroll ? lines : lines * 16.0);
        perNotch = System.Math.Min(perNotch, sv.ViewportHeight); // 한 번에 최대 한 화면.
        sv.ScrollToVerticalOffset(sv.VerticalOffset - notches * perNotch);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    // 현재 전체 문서 상태를 JSON 문자열로 캡처한다.
    private string CaptureSnapshot()
    {
        var project = new ComicProjectData
        {
            Title = ComicTitleTextBox.Text.Trim(),
            CurrentPageIndex = _currentPageIndex,
            // 이미지가 실행 파일 폴더(또는 하위)면 상대 경로로, 아니면 절대 경로로 저장한다.
            // → 실행 파일·자동저장을 이미지와 함께 옮겨도 다음 실행 때 그대로 열린다.
            Pages = CaptureProjectPages(null),
            FlowText = _flow.Clone(),
            VnTemplates = _vnTemplates.Select(t => CopyPageForStorage(t, null)).ToList(),
            VnEditingIndex = _editingTemplate != null ? _vnTemplates.IndexOf(_editingTemplate) : -1
        };
        return JsonSerializer.Serialize(project);
    }

    private void AutoSave(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_autosavePath)!);
            File.WriteAllText(_autosavePath, json);
        }
        catch
        {
            // 자동 저장 실패는 무시(앱 동작에는 영향 없음).
        }
    }

    private void TryLoadAutosave()
    {
        try
        {
            // 새 이름이 있으면 그것을, 없으면 옛 이름(autosave.nvjson)을 복원한다.
            var path = File.Exists(_autosavePath)
                ? _autosavePath
                : (File.Exists(_legacyAutosavePath) ? _legacyAutosavePath : null);
            if (path == null)
            {
                return;
            }

            RestoreSnapshot(File.ReadAllText(path));
            UpdateStatus("이전 작업을 불러왔습니다.");
        }
        catch
        {
            // 손상된 자동 저장본은 무시하고 기본 페이지를 유지한다.
        }
    }

    // 스냅샷으로 문서를 되돌린다(불러오기와 동일한 재구성).
    private void RestoreSnapshot(string json)
    {
        var project = JsonSerializer.Deserialize<ComicProjectData>(json);
        if (project == null || project.Pages.Count == 0)
        {
            return;
        }

        ComicTitleTextBox.Text = project.Title;
        ReplacePages(project.Pages); // _editingTemplate = null로 초기화됨.
        // VN 템플릿 복원(자동저장·실행취소 JSON 모두 VnTemplates를 포함하므로 그대로 교체).
        _vnTemplates.Clear();
        foreach (var t in project.VnTemplates)
        {
            _vnTemplates.Add(t);
        }

        _currentPageIndex = Math.Clamp(project.CurrentPageIndex, 0, _pages.Count - 1);

        // 편집 대상 복원: 스냅샷이 템플릿 편집 중이었다면 그 템플릿을 캔버스로 연다.
        if (project.VnEditingIndex >= 0 && project.VnEditingIndex < _vnTemplates.Count)
        {
            _editingTemplate = _vnTemplates[project.VnEditingIndex];
            LoadPage(_editingTemplate);
            _isSwitchingEditTarget = true;
            VnTemplateListBox.SelectedIndex = project.VnEditingIndex;
            PageListBox.SelectedIndex = -1;
            _isSwitchingEditTarget = false;
        }
        else
        {
            LoadPage(_pages[_currentPageIndex]);
        }

        ApplyFlowText(project.FlowText); // 본문 텍스트·서식 복원(실행취소/자동저장 포함).
        UpdatePageList();
    }

    // 히스토리 기준선을 현재 상태로 다시 잡는다(전체 재직렬화 + 경량 서명).
    private void ResetHistoryBaseline()
    {
        _historyStructuralPending = false;
        _baseline = CaptureHistorySnapshot(forceFull: true);
        _lastChangeSignature = ComputeChangeSignature();
        // 기준선을 새로 잡았다는 건 현재 상태가 곧 참조점이라는 뜻 → 미저장 델타 없음.
        _autosavePending = false;
        _autosaveIdleTicks = 0;
        _autosaveAgeTicks = 0;
    }

    // 현재 문서를 '페이지별 JSON 목록' 스냅샷으로 캡처한다.
    // 안 바뀐 페이지(현재 페이지가 아니고 구조 변경도 없음)는 직전 기준선의 문자열을 그대로 공유해
    // 직렬화·메모리 비용을 '바뀐 페이지' 하나로 한정한다.
    private HistorySnapshot CaptureHistorySnapshot(bool forceFull = false)
    {
        SaveCurrentPageState();
        var snap = new HistorySnapshot
        {
            Title = ComicTitleTextBox.Text.Trim(),
            CurrentIndex = _currentPageIndex,
            PageJsons = new List<string>(_pages.Count),
            FlowJson = JsonSerializer.Serialize(_flow),
            VnTemplatesJson = JsonSerializer.Serialize(_vnTemplates.Select(t => CopyPageForStorage(t, null)).ToList()),
            VnEditingIndex = _editingTemplate != null ? _vnTemplates.IndexOf(_editingTemplate) : -1
        };

        var full = forceFull || _baseline == null || _historyStructuralPending
                   || _pages.Count != _baseline.PageJsons.Count;
        for (var i = 0; i < _pages.Count; i++)
        {
            if (full || i == _currentPageIndex)
            {
                snap.PageJsons.Add(SerializePageForHistory(_pages[i]));
            }
            else
            {
                snap.PageJsons.Add(_baseline!.PageJsons[i]); // 안 바뀐 페이지: 직전 문자열 공유
            }
        }

        _historyStructuralPending = false;
        return snap;
    }

    // 페이지 한 장을 저장과 동일한 형식(이미지 경로 변환 포함)으로 직렬화한다.
    private string SerializePageForHistory(ComicPageData page)
        => JsonSerializer.Serialize(CopyPageForStorage(page, null));

    // 스냅샷(페이지별 JSON)을 RestoreSnapshot이 읽는 전체 프로젝트 JSON으로 조립한다.
    // 페이지 JSON은 이미 만들어져 있으므로 문자열 연결만으로 끝난다(재직렬화 없음).
    private string AssembleFullJson(HistorySnapshot snapshot)
    {
        var pages = string.Join(",", snapshot.PageJsons);
        var title = JsonSerializer.Serialize(snapshot.Title); // 따옴표·이스케이프 포함
        var flow = string.IsNullOrEmpty(snapshot.FlowJson) ? "{}" : snapshot.FlowJson;
        // 스냅샷 시점의 템플릿·편집 대상까지 포함해야 실행취소/다시실행이 템플릿 편집도 되돌린다.
        var templates = string.IsNullOrEmpty(snapshot.VnTemplatesJson) ? "[]" : snapshot.VnTemplatesJson;
        return $"{{\"Title\":{title},\"CurrentPageIndex\":{snapshot.CurrentIndex},\"Pages\":[{pages}],\"FlowText\":{flow},\"VnTemplates\":{templates},\"VnEditingIndex\":{snapshot.VnEditingIndex}}}";
    }

    // 변화 감지용 경량 서명. 편집은 현재 페이지에서만 일어나므로, 전체가 아니라
    // 제목 + 현재 페이지 인덱스 + 페이지 수 + 모든 페이지 이름 + '현재 페이지'의 JSON만으로 변화를 판별한다.
    // (다른 페이지의 '내용'은 현재 페이지가 되지 않는 한 바뀌지 않고, 이름·순서·개수는 위 항목으로 잡힌다.)
    private string ComputeChangeSignature()
    {
        SaveCurrentPageState();
        var names = string.Join("", _pages.Select(p => p.Name));
        var current = _currentPageIndex >= 0 && _currentPageIndex < _pages.Count
            ? JsonSerializer.Serialize(_pages[_currentPageIndex])
            : string.Empty;
        // VN 템플릿 편집·추가·삭제·전환도 변화로 잡아 실행취소 경계를 만든다(템플릿은 보통 적어 비용 작음).
        var vn = $"{(_editingTemplate != null ? _vnTemplates.IndexOf(_editingTemplate) : -1)}|{JsonSerializer.Serialize(_vnTemplates)}";
        return $"{ComicTitleTextBox.Text}|{_currentPageIndex}|{_pages.Count}|{names}|{current}|{JsonSerializer.Serialize(_flow)}|{vn}";
    }

    // 마지막 기준선과 달라졌으면 변경분을 undo 스택에 쌓는다.
    // 마우스 버튼을 누르고 있는 동안(드래그/슬라이더 조작 중)은 건너뛰어 한 동작을 한 단계로 묶는다.
    private void CaptureHistoryIfChanged()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        // 마지막 캡처 이후 입력이 전혀 없었으면 건너뛴다(idle 비용 제거).
        if (!_historyDirty)
        {
            return;
        }

        _historyDirty = false;

        // 싼 서명으로 먼저 판별 → 클릭/선택 등 문서가 안 바뀐 입력에서는 전체 직렬화를 생략한다(페이지가 많을수록 큰 절약).
        var sig = ComputeChangeSignature();
        if (sig == _lastChangeSignature)
        {
            return;
        }
        _lastChangeSignature = sig;

        // 실제 변경분만 캡처(바뀐 페이지 한 장만 새로 직렬화, 나머지는 직전 스냅샷 공유).
        var snapshot = CaptureHistorySnapshot();
        var prev = _baseline;
        _baseline = snapshot;

        if (prev != null)
        {
            _undoStack.Add(prev);
            if (_undoStack.Count > MaxHistory)
            {
                _undoStack.RemoveAt(0);
            }
        }

        _redoStack.Clear();
        UpdateUndoRedoButtons();

        // 자동저장이 꺼져 있으면 디스크 쓰기를 표시하지 않는다(실행취소 기록은 위에서 이미 메모리에 남김).
        if (_autosaveDisabled)
        {
            return;
        }

        // 디스크 쓰기는 즉시 하지 않고 '미저장'으로 표시만 한다(편집이 멈춘 뒤 한 번에 flush → 자동저장 I/O 디바운스).
        if (!_autosavePending)
        {
            _autosavePending = true;
            _autosaveAgeTicks = 0;
        }
        _autosaveIdleTicks = 0;
    }

    // 히스토리 타이머 틱: 변경분을 메모리에 기록한 뒤, 미저장분이 있으면 디바운스 조건에서 디스크에 flush한다.
    private void HistoryTick()
    {
        // 동영상 프레임 캡처로 디스패처를 펌프하거나 내보내기 중이면 끼어들지 않는다(부분/잡음 상태 캡처 방지).
        if (_pumpingMedia || _exporting)
        {
            return;
        }

        CaptureHistoryIfChanged();

        if (!_autosavePending)
        {
            return;
        }

        _autosaveIdleTicks++;
        _autosaveAgeTicks++;
        // 편집이 잠시 멈췄거나(유휴), 연속 편집이라도 상한에 도달하면 한 번에 저장한다.
        if (_autosaveIdleTicks >= AutosaveFlushIdleTicks || _autosaveAgeTicks >= AutosaveFlushMaxTicks)
        {
            FlushAutosave();
        }
    }

    // 미저장 상태를 디스크에 반영한다(전체 JSON 조립도 이때 한 번만 한다).
    private void FlushAutosave()
    {
        if (!_autosavePending || _baseline == null)
        {
            return;
        }

        _autosavePending = false;
        _autosaveIdleTicks = 0;
        _autosaveAgeTicks = 0;
        AutoSave(AssembleFullJson(_baseline));
    }

    private void Undo()
    {
        CaptureHistoryIfChanged();
        if (_undoStack.Count == 0)
        {
            UpdateStatus("실행 취소할 작업이 없습니다.");
            return;
        }

        if (_baseline != null)
        {
            _redoStack.Add(_baseline);
        }
        var target = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        // 재구성 전 현재 선택을 (페이지·인덱스로) 기억했다가, 재구성 후 같은 위치 오브젝트를 다시 선택한다.
        var sel = CaptureSelectionRef();
        var page = _currentPageIndex;
        RestoreSnapshot(AssembleFullJson(target));
        RestoreSelectionAfterRebuild(sel, page);

        // 복원한 스냅샷이 곧 새 기준선이다(페이지별 JSON이 그대로 일치하므로 재직렬화 불필요).
        _baseline = target;
        _lastChangeSignature = ComputeChangeSignature();
        _historyStructuralPending = false;
        UpdateUndoRedoButtons();
        UpdateStatus("실행을 취소했습니다.");
    }

    private void Redo()
    {
        CaptureHistoryIfChanged();
        if (_redoStack.Count == 0)
        {
            UpdateStatus("다시 실행할 작업이 없습니다.");
            return;
        }

        if (_baseline != null)
        {
            _undoStack.Add(_baseline);
        }
        var target = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        var sel = CaptureSelectionRef();
        var page = _currentPageIndex;
        RestoreSnapshot(AssembleFullJson(target));
        RestoreSelectionAfterRebuild(sel, page);

        _baseline = target;
        _lastChangeSignature = ComputeChangeSignature();
        _historyStructuralPending = false;
        UpdateUndoRedoButtons();
        UpdateStatus("다시 실행했습니다.");
    }

    // 재구성으로 오브젝트 인스턴스가 새로 만들어지므로, 선택을 고유 ID로 식별해 둔다(인덱스보다 견고).
    private (SelectionKind Kind, string Id) CaptureSelectionRef()
    {
        return _selectionKind switch
        {
            SelectionKind.Panel when _selectedPanel != null => (SelectionKind.Panel, _selectedPanel.Id),
            SelectionKind.Image when _selectedImage != null => (SelectionKind.Image, _selectedImage.Id),
            SelectionKind.Bubble when _selectedBubble != null => (SelectionKind.Bubble, _selectedBubble.Id),
            _ => (SelectionKind.None, string.Empty)
        };
    }

    // 재구성 후 같은 ID의 오브젝트를 다시 선택한다(페이지가 바뀌었거나 대상이 사라졌으면 선택 해제).
    private void RestoreSelectionAfterRebuild((SelectionKind Kind, string Id) sel, int page)
    {
        var restored = false;
        if (_currentPageIndex == page && !string.IsNullOrEmpty(sel.Id))
        {
            switch (sel.Kind)
            {
                case SelectionKind.Panel:
                    var panel = _panels.FirstOrDefault(p => p.Id == sel.Id);
                    if (panel != null) { SelectPanel(panel); restored = true; }
                    break;
                case SelectionKind.Image:
                    var image = _panels.SelectMany(p => p.Images).FirstOrDefault(i => i.Id == sel.Id);
                    if (image != null) { SelectImage(image); restored = true; }
                    break;
                case SelectionKind.Bubble:
                    var bubble = _panels.SelectMany(p => p.Bubbles).FirstOrDefault(b => b.Id == sel.Id);
                    if (bubble != null) { SelectBubble(bubble); restored = true; }
                    break;
            }
        }

        if (!restored)
        {
            // 복원 불가: 선택 상태를 깔끔히 비운다(재구성으로 _selectedX는 이미 null).
            _selectionKind = SelectionKind.None;
            UpdateSelectionVisuals();
            return;
        }

        // 말풍선 선택 박스/핸들 위치는 overlay.TransformToVisual(레이아웃 의존)에 기대므로,
        // 재구성 직후엔 부정확하다. 레이아웃이 끝난 뒤 한 번 더 위치를 잡는다.
        Dispatcher.BeginInvoke(
            new Action(() => { if (_selectionKind != SelectionKind.None) UpdateSelectionVisuals(); }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateUndoRedoButtons()
    {
        if (UndoMenuItem != null)
        {
            UndoMenuItem.IsEnabled = _undoStack.Count > 0;
        }

        if (RedoMenuItem != null)
        {
            RedoMenuItem.IsEnabled = _redoStack.Count > 0;
        }
    }

    private void PageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageFit();
    }

    // "쪽 맞춤"은 페이지 전체가 보이도록, "폭 맞춤"은 페이지 너비가 뷰에 차도록 맞춘다(세로는 스크롤).
    private void UpdatePageFit()
    {
        if (PageFrame == null || PageScrollViewer == null)
        {
            return;
        }

        var pageFit = PageFitMenuItem?.IsChecked == true;
        var widthFit = PageWidthFitMenuItem?.IsChecked == true;
        if (!pageFit && !widthFit)
        {
            PageFrame.LayoutTransform = Transform.Identity;
            return;
        }

        // ScrollViewer Padding(28) 양쪽을 빼고, 스크롤바 깜빡임 방지를 위한 여유를 둔다.
        var availableWidth = PageScrollViewer.ActualWidth - 56 - 4;
        var availableHeight = PageScrollViewer.ActualHeight - 56 - 4;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        // 폭 맞춤: 너비 비율만(세로 넘치면 스크롤). 쪽 맞춤: 가로·세로 중 작은 비율(전체 보임).
        var scale = widthFit
            ? availableWidth / _pageWidth
            : Math.Min(availableWidth / _pageWidth, availableHeight / _pageHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        PageFrame.LayoutTransform = new ScaleTransform(scale, scale);
        CullOffscreenPanels(); // 배율 변경으로 보이는 범위가 달라지므로 다시 컬링.
    }

    private void ComicTitleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateWindowTitle();
    }

    // 앱 버전(창 제목 등에 표시). csproj의 <Version> 한 곳에서 런타임에 읽는다(하드코딩 중복 제거).
    private static readonly string AppVersion = ReadAppVersion();

    private static string ReadAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v0.0.0";
    }

    private void UpdateWindowTitle()
    {
        var title = ComicTitleTextBox?.Text?.Trim();
        Title = string.IsNullOrEmpty(title)
            ? $"KomaForge {AppVersion} - Comic Layout"
            : $"{title} - KomaForge {AppVersion}";
    }

    private string GetDefaultProjectFileName()
    {
        var title = ComicTitleTextBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return "KomaForgeProject.kfjson";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            title = title.Replace(invalid, '_');
        }

        return title + ".kfjson";
    }

}
