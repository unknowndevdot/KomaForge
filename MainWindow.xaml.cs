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
    private double _pageWidth = 820;
    private double _pageHeight = 1120;
    private double _inspectorWidth = 360; // 드래그로 조절한 인스펙터 폭(토글 복원용).
    // 마우스 호버 시 '클릭하면 선택될 대상'을 보여주는 작은 툴팁.
    private System.Windows.Controls.Primitives.Popup? _hoverPopup;
    private TextBlock? _hoverText;
    // 실행 취소/다시 실행: 전체 문서 상태를 JSON 스냅샷으로 보관한다.
    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private string _lastSnapshot = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _historyTimer;
    private const int MaxHistory = 60;
    // 입력(마우스·키)이 있을 때만 true가 되어, idle 상태에서 전체 문서를 매 틱 직렬화하는 것을 막는다.
    private bool _historyDirty;

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
    private bool _isDraggingPanel;
    private bool _isDraggingPanelImage;
    private bool _isDraggingBubble;
    private bool _isLoadingInspector;
    private bool _isUpdatingBubbleList;
    private bool _isUpdatingBubbleTailList;
    private bool _isUpdatingImageList;
    private bool _isUpdatingPageList;
    private int _nextPanelNumber = 1;
    private readonly List<ComicPageData> _pages = new();
    private int _currentPageIndex;
    private string? _projectBaseDirectory;
    // 현재 불러왔거나 저장한 프로젝트 파일 전체 경로(Ctrl+S 덮어쓰기 대상). 없으면 다른 이름으로 저장.
    private string? _projectFilePath;
    // 꼬리 편집용 세 점 핸들은 칸 경계 클리핑을 피하기 위해 페이지 레이어(PageOverlay)에
    // 올려 두는 싱글톤이다. 선택된 말풍선의 선택된 꼬리에만 표시된다.
    private Thumb? _tailStartHandle;
    private Thumb? _tailMidHandle;
    private Thumb? _tailEndHandle;
    // 말풍선 선택 박스/리사이즈 핸들도 칸 경계에 잘리지 않도록 PageOverlay에 싱글톤으로 둔다.
    private Border? _bubbleSelectionBox;
    private Thumb? _bubbleResizeHandle;
    // 텍스트 영역(여백)을 직접 조절하는 4모서리 핸들(싱글톤, PageOverlay).
    private Thumb? _textRegionTopLeft;
    private Thumb? _textRegionTopRight;
    private Thumb? _textRegionBottomLeft;
    private Thumb? _textRegionBottomRight;
    // 칸 사변형 모서리 조절 핸들(싱글톤, PageOverlay). 인덱스 0=TL,1=TR,2=BR,3=BL.
    private Thumb[]? _panelCornerHandles;
    // 포터블: 설정·자동저장을 실행 파일과 같은 폴더에 둔다(단일 파일 게시에서도 exe 폴더를 가리킴).
    private readonly string _windowSettingsPath = Path.Combine(
        AppContext.BaseDirectory,
        "window-settings.json");
    // 작업 내용 자동 저장(다음 실행 시 자동 복원). 명시적 저장/불러오기와는 별개.
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
        LoadWindowSettings();
        PopulateColorCombo(BubbleFillColorComboBox, "#000000");
        PopulateColorCombo(BubbleStrokeColorComboBox, "#FFFFFF");
        PopulateColorCombo(BubbleBackgroundColorComboBox, "#FFFFFF");
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

        // 히스토리 초기화: 현재 상태를 기준선으로 삼고, 변화 감지 타이머를 돌린다.
        _lastSnapshot = CaptureSnapshot();
        UpdateUndoRedoButtons();
        _historyTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _historyTimer.Tick += (_, _) => CaptureHistoryIfChanged();
        _historyTimer.Start();

        // 창 레이아웃이 끝난 뒤 기준선을 다시 잡아 시작 시 생기는 잡음 히스토리를 제거한다.
        Loaded += (_, _) =>
        {
            _lastSnapshot = CaptureSnapshot();
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateUndoRedoButtons();
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

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (e.Key == Key.Z && !shift)
            {
                Undo();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Y || (e.Key == Key.Z && shift))
            {
                Redo();
                e.Handled = true;
                return;
            }

            // Ctrl+S: 현재 파일에 덮어쓰기 저장(경로 없으면 다른 이름으로 저장).
            if (e.Key == Key.S)
            {
                SaveProjectToCurrentOrPrompt();
                e.Handled = true;
                return;
            }

            // Ctrl+R: 선택 대상 리셋(이미지=100% 원본, 칸=기본 사각형).
            if (e.Key == Key.R)
            {
                ResetSelectedToDefault();
                e.Handled = true;
                return;
            }
        }

        // L: 선택 오브젝트 잠금 토글.
        // 텍스트 입력 중이거나 수정자 키(Ctrl/Alt)와 함께일 때는 단축키로 동작하지 않는다.
        if (e.Key == Key.L &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                return; // 글자 입력으로 둔다.
            }

            ToggleSelectedLock();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
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

            var direction = e.Key == Key.Up ? -1 : 1;

            // 인스펙터가 닫혀 있으면 위/아래 키로 페이지를 넘긴다(선택 상태와 무관).
            if (!IsInspectorOpen())
            {
                NavigatePage(direction);
                e.Handled = true;
                return;
            }

            // 인스펙터가 열려 있으면 선택 대상의 순서를 위/아래로 옮긴다.
            switch (_selectionKind)
            {
                case SelectionKind.Bubble when _selectedBubble != null:
                    MoveSelectedBubble(direction);
                    e.Handled = true;
                    return;
                case SelectionKind.Image when _selectedImage != null:
                    MoveSelectedImage(direction);
                    e.Handled = true;
                    return;
                case SelectionKind.Panel when _selectedPanel != null:
                    MoveSelectedPanel(direction);
                    e.Handled = true;
                    return;
            }
        }
    }

    private bool IsInspectorOpen() => InspectorPanel.Visibility == Visibility.Visible;

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        CreateLayoutFromPattern(LayoutPatternTextBox.Text);
    }

    private void PageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPageList || PageListBox.SelectedIndex < 0 || PageListBox.SelectedIndex == _currentPageIndex)
        {
            return;
        }

        SaveCurrentPageState();
        _currentPageIndex = PageListBox.SelectedIndex;
        LoadPage(_pages[_currentPageIndex]);
        UpdateStatus($"{_pages[_currentPageIndex].Name} 페이지를 열었습니다.");
    }

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPageState();
        _pages.Add(new ComicPageData { Name = $"Page {_pages.Count + 1}", PageWidth = _pageWidth, PageHeight = _pageHeight });
        _currentPageIndex = _pages.Count - 1;
        LoadPage(_pages[_currentPageIndex]);
        // 새 페이지를 기본 칸 구성으로 자동 채운다(패턴이 비어 있으면 빈 페이지 유지).
        CreateLayoutFromPattern(LayoutPatternTextBox.Text);
        ClearSelection();
        UpdatePageList();
        UpdateStatus("새 페이지를 기본 칸 구성으로 추가했습니다.");
    }

    private void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count <= 1)
        {
            UpdateStatus("페이지는 최소 1개가 필요합니다.");
            return;
        }

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
        var target = _currentPageIndex + direction;
        if (_currentPageIndex < 0 || target < 0 || target >= _pages.Count)
        {
            return;
        }

        // 현재 편집 내용을 페이지 데이터에 반영한 뒤 위치만 교환한다(표시 중인 페이지는 그대로).
        SaveCurrentPageState();
        (_pages[_currentPageIndex], _pages[target]) = (_pages[target], _pages[_currentPageIndex]);
        _currentPageIndex = target;
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
        _pageWidth = Math.Clamp(width, 100, 5000);
        _pageHeight = Math.Clamp(height, 100, 5000);

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

    private void PageFitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdatePageFit();
    }

    private void BlackBackgroundCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyPageBackground();
    }

    // 페이지 배경을 검은색/흰색으로 적용한다(내보내기 결과 배경에도 반영됨).
    private void ApplyPageBackground()
    {
        var black = BlackBackgroundCheckBox?.IsChecked == true;
        var brush = black ? Brushes.Black : Brushes.White;
        if (PageSurface != null)
        {
            PageSurface.Background = brush;
        }

        if (PageFrame != null)
        {
            PageFrame.Background = brush;
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

        // 인스펙터가 열려 있으면: 말풍선 위에서는 휠로 말풍선 크기를 확대/축소한다.
        var bubble = FindBubbleAt(e.OriginalSource as DependencyObject);
        if (bubble != null)
        {
            ZoomBubble(bubble, e);
            return;
        }

        // 이미지가 선택된 칸 위에서는 휠로 이미지 확대/축소(frame의 ZoomPanelImage가 처리하도록 둔다).
        // 그 외 빈 영역에서는 페이지를 넘기지 않고 ScrollViewer 기본 스크롤에 맡긴다.
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

    // 인스펙터 안의 리스트 등이 휠을 먼저 소비해 바깥이 안 스크롤되는 문제를 막기 위해,
    // 터널링 단계에서 인스펙터 ScrollViewer를 직접 스크롤한다.
    private void InspectorScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        InspectorScrollViewer.ScrollToVerticalOffset(InspectorScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
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
            Pages = CaptureProjectPages(null)
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
        _pages.Clear();
        _pages.AddRange(project.Pages);
        _currentPageIndex = Math.Clamp(project.CurrentPageIndex, 0, _pages.Count - 1);
        LoadPage(_pages[_currentPageIndex]);
        UpdatePageList();
    }

    // 마지막 기준선과 달라졌으면 변경분을 undo 스택에 쌓는다.
    // 마우스 버튼을 누르고 있는 동안(드래그/슬라이더 조작 중)은 건너뛰어 한 동작을 한 단계로 묶는다.
    private void CaptureHistoryIfChanged()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        // 마지막 캡처 이후 입력이 전혀 없었으면 전체 직렬화를 건너뛴다(idle 비용 제거).
        if (!_historyDirty)
        {
            return;
        }

        _historyDirty = false;
        var snapshot = CaptureSnapshot();
        if (snapshot == _lastSnapshot)
        {
            return;
        }

        _undoStack.Add(_lastSnapshot);
        if (_undoStack.Count > MaxHistory)
        {
            _undoStack.RemoveAt(0);
        }

        _lastSnapshot = snapshot;
        _redoStack.Clear();
        UpdateUndoRedoButtons();
        AutoSave(snapshot);
    }

    private void Undo()
    {
        CaptureHistoryIfChanged();
        if (_undoStack.Count == 0)
        {
            UpdateStatus("실행 취소할 작업이 없습니다.");
            return;
        }

        _redoStack.Add(_lastSnapshot);
        var target = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(target);
        _lastSnapshot = CaptureSnapshot();
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

        _undoStack.Add(_lastSnapshot);
        var target = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(target);
        _lastSnapshot = CaptureSnapshot();
        UpdateUndoRedoButtons();
        UpdateStatus("다시 실행했습니다.");
    }

    private void UpdateUndoRedoButtons()
    {
        if (UndoButton != null)
        {
            UndoButton.IsEnabled = _undoStack.Count > 0;
        }

        if (RedoButton != null)
        {
            RedoButton.IsEnabled = _redoStack.Count > 0;
        }
    }

    private void PageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageFit();
    }

    // "페이지 쪽 맞춤"이 켜져 있으면 페이지 전체가 보이도록 뷰 영역에 맞춰 축소/확대한다.
    private void UpdatePageFit()
    {
        if (PageFrame == null || PageScrollViewer == null)
        {
            return;
        }

        if (PageFitCheckBox?.IsChecked != true)
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

        var scale = Math.Min(availableWidth / _pageWidth, availableHeight / _pageHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        PageFrame.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void ComicTitleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var title = ComicTitleTextBox?.Text?.Trim();
        Title = string.IsNullOrEmpty(title) ? "KomaForge - Comic Layout" : $"{title} - KomaForge";
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

    private void ExportPagesAsImages_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0)
        {
            UpdateStatus("내보낼 페이지가 없습니다.");
            return;
        }

        // 현재 편집 중인 페이지 상태를 먼저 저장한다.
        SaveCurrentPageState();

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

                var bitmap = RenderPageToBitmap();

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
        }

        if (exported > 0)
        {
            UpdateStatus($"{exported}개 페이지를 이미지로 내보냈습니다: {folder}");
        }
    }

    // 현재 페이지를 페이지 크기 그대로 비트맵으로 렌더한다.
    private RenderTargetBitmap RenderPageToBitmap()
    {
        var width = (int)Math.Ceiling(_pageWidth);
        var height = (int)Math.Ceiling(_pageHeight);
        var pageRect = new Rect(0, 0, _pageWidth, _pageHeight);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // 배경을 직접 그린다(페이지별 검/흰). PageSurface는 PageFrame 테두리(1px)만큼 오프셋이 있어
            // VisualBrush로 쓰면 좌/상에 1px 투명 여백이 생기므로, 그리드 원점(0,0)에 있는 PanelCanvas를 렌더한다.
            var background = BlackBackgroundCheckBox?.IsChecked == true ? Brushes.Black : Brushes.White;
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

                var still = GetVideoStillFrame(ResolveProjectPath(image.Path));
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
            _pages.Clear();
            _pages.AddRange(project.Pages);
            _currentPageIndex = Math.Clamp(project.CurrentPageIndex, 0, _pages.Count - 1);
            LoadPage(_pages[_currentPageIndex]);
            UpdatePageList();
            UpdateStatus("프로젝트를 불러왔습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로젝트를 불러올 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddPanel_Click(object sender, RoutedEventArgs e)
    {
        var panel = CreatePanel(_nextPanelNumber++, PanelXSlider.Value, PanelYSlider.Value, PanelWidthSlider.Value, PanelHeightSlider.Value);
        AddPanel(panel);
        SelectPanel(panel);
        UpdateStatus($"{panel.Number}번 칸을 추가했습니다.");
    }

    private void DeletePanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("삭제할 칸을 선택하세요.");
            return;
        }

        RemovePanel(_selectedPanel);
        UpdatePanelOrder();

        _selectedPanel = _panels.LastOrDefault();
        _selectedBubble = null;

        if (_selectedPanel != null)
        {
            SelectPanel(_selectedPanel);
        }
        else
        {
            UpdatePanelList();
        }

        UpdateSelectionLabels();
        UpdateSelectionVisuals();
        UpdateLayoutSummary();
        UpdateStatus("칸을 삭제했습니다.");
    }

    // 칸과 그 말풍선을 화면/목록에서 제거한다(선택 상태는 호출부에서 정리).
    private void RemovePanel(ComicPanel panel)
    {
        foreach (var bubble in panel.Bubbles.ToList())
        {
            RemoveBubbleFromCurrentParent(bubble);
        }

        PanelCanvas.Children.Remove(panel.Frame);
        _panels.Remove(panel);
        PanelListBox.Items.Remove(panel);

        if (_selectedImage?.OwnerPanel == panel)
        {
            _selectedImage = null;
        }

        if (_selectedPanel == panel)
        {
            _selectedPanel = null;
        }
    }

    // 기존 칸의 내용을 유지한 채 위치/크기만 격자 슬롯에 맞춘다.
    private void ApplyPanelBounds(ComicPanel panel, double x, double y, double width, double height)
    {
        panel.Frame.Width = width;
        panel.Frame.Height = height;
        UpdatePanelImageSizes(panel);
        Canvas.SetLeft(panel.Frame, x);
        Canvas.SetTop(panel.Frame, y);
        UpdateFreeBubblesForPanel(panel);
    }

    private void RenumberPanels()
    {
        for (var index = 0; index < _panels.Count; index++)
        {
            _panels[index].Number = index + 1;
            _panels[index].Placeholder.Text = $"{index + 1}번 칸";
        }

        _nextPanelNumber = _panels.Count + 1;
    }

    private void AddImageToPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("이미지를 넣을 칸을 먼저 선택하세요.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "칸에 넣을 이미지/동영상 선택",
            Filter = "이미지·동영상 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm;*.mov;*.avi;*.mkv;*.m4v)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm;*.mov;*.avi;*.mkv;*.m4v|모든 파일 (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            PanelImage? lastImage = null;
            foreach (var fileName in dialog.FileNames)
            {
                lastImage = AddPanelImage(_selectedPanel, fileName);
            }

            if (lastImage != null)
            {
                SelectImage(lastImage);
            }

            UpdateStatus($"{_selectedPanel.Number}번 칸에 이미지 {dialog.FileNames.Length}개를 넣었습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"이미지를 열 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearPanelImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("이미지를 제거할 칸을 먼저 선택하세요.");
            return;
        }

        if (_selectedImage == null || _selectedImage.OwnerPanel != _selectedPanel)
        {
            UpdateStatus("제거할 이미지를 먼저 선택하세요.");
            return;
        }

        DeleteSelectedImage();
    }

    private void AddBubble_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("말풍선을 넣을 칸을 먼저 선택하세요.");
            return;
        }

        // 새 말풍선은 칸의 정중앙에 생성한다(위치는 칸 기준 상대 좌표).
        var bubbleWidth = BubbleWidthSlider.Value;
        var bubbleHeight = BubbleHeightSlider.Value;
        var bubble = CreateSpeechBubble(
            _selectedPanel,
            "대사를 입력하세요",
            bubbleWidth,
            bubbleHeight,
            BubbleFontSlider.Value,
            _selectedPanel.Frame.Width / 2 - bubbleWidth / 2,
            _selectedPanel.Frame.Height / 2 - bubbleHeight / 2);

        AttachBubbleToPanelOverlay(bubble);
        if (BubbleCropCheckBox.IsChecked != true)
        {
            SetBubbleCrop(bubble, false);
        }

        _selectedPanel.Bubbles.Add(bubble);
        UpdateMergedBubbleOutlines();
        UpdateBubbleOrder(_selectedPanel);
        UpdateBubbleList(_selectedPanel);
        SelectBubble(bubble);
        UpdateStatus($"{_selectedPanel.Number}번 칸에 말풍선을 추가했습니다.");
    }

    private void DeleteBubble_Click(object sender, RoutedEventArgs e)
    {
        if (!DeleteSelectedBubble())
        {
            UpdateStatus("삭제할 말풍선을 선택하세요.");
        }
    }

    private void PanelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPanelList)
        {
            return;
        }

        if (PanelListBox.SelectedItem is ComicPanel panel)
        {
            SelectPanel(panel);
        }
    }

    private void ImageListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingImageList)
        {
            return;
        }

        if (ImageListBox.SelectedItem is PanelImage image)
        {
            SelectImage(image);
        }
    }

    private void MoveImageUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedImage(-1);
    }

    private void MoveImageDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedImage(1);
    }

    private void MovePanelUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedPanel(-1);
    }

    private void MovePanelDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedPanel(1);
    }

    private void MoveBubbleUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedBubble(-1);
    }

    private void MoveBubbleDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedBubble(1);
    }

    private void AddBubbleTail_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBubble == null)
        {
            UpdateStatus("꼬리를 추가할 말풍선을 먼저 선택하세요.");
            return;
        }

        var startX = _selectedBubble.Container.Width / 2;
        var startY = _selectedBubble.Container.Height / 2;
        var tail = new BubbleTail
        {
            StartX = startX,
            StartY = startY,
            MidX = (startX + BubbleTailXSlider.Value) / 2,
            MidY = (startY + BubbleTailYSlider.Value) / 2,
            X = BubbleTailXSlider.Value,
            Y = BubbleTailYSlider.Value,
            Width = BubbleTailWidthSlider.Value
        };
        _selectedBubble.Tails.Add(tail);
        SelectBubbleTail(tail);
        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        UpdateStatus("말풍선 꼬리를 추가했습니다.");
    }

    private void DeleteBubbleTail_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedBubbleTail();
    }

    private void MoveBubbleTailUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedBubbleTail(-1);
    }

    private void MoveBubbleTailDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedBubbleTail(1);
    }

    private void MoveSelectedBubbleTail(int direction)
    {
        if (_selectedBubble == null || _selectedBubbleTail == null || !_selectedBubble.Tails.Contains(_selectedBubbleTail))
        {
            UpdateStatus("순서를 바꿀 꼬리를 먼저 선택하세요.");
            return;
        }

        var tails = _selectedBubble.Tails;
        var index = tails.IndexOf(_selectedBubbleTail);
        var nextIndex = index + direction;
        if (index < 0 || nextIndex < 0 || nextIndex >= tails.Count)
        {
            return;
        }

        tails.RemoveAt(index);
        tails.Insert(nextIndex, _selectedBubbleTail);
        UpdateBubbleTailList(_selectedBubble);
        BubbleTailListBox.SelectedItem = _selectedBubbleTail;
        UpdateStatus("꼬리 순서를 변경했습니다.");
    }

    private void BubbleTailListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingBubbleTailList)
        {
            return;
        }

        if (BubbleTailListBox.SelectedItem is BubbleTail tail)
        {
            SelectBubbleTail(tail);
        }
    }

    private void BubbleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingBubbleList)
        {
            return;
        }

        if (BubbleListBox.SelectedItem is SpeechBubble bubble)
        {
            SelectBubble(bubble);
        }
    }

    private void PanelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedPanel != null && !_isLoadingInspector)
        {
            ApplyPanelValues(_selectedPanel);
        }

        UpdateInspectorLabels();
    }

    private void BubbleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_selectedBubble != null && !_isLoadingInspector)
        {
            ApplyBubbleValues(_selectedBubble);
        }

        UpdateInspectorLabels();
    }

    private void BubbleCropCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        SetBubbleCrop(_selectedBubble, BubbleCropCheckBox.IsChecked == true);
    }

    private void BubbleOutlineCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.TextBlock.OutlineEnabled = BubbleOutlineCheckBox.IsChecked == true;
        UpdateStatus(_selectedBubble.TextBlock.OutlineEnabled ? "글자 아웃라인을 켰습니다." : "글자 아웃라인을 껐습니다.");
    }

    private void BubbleTailInwardCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        if (_selectedBubbleTail == null)
        {
            UpdateStatus("안으로 깎을 꼬리를 먼저 선택하세요.");
            return;
        }

        _selectedBubbleTail.TailInward = BubbleTailInwardCheckBox.IsChecked == true;

        // ON/OFF 전환 시 시작점(넓은 쪽)과 끝점(뾰족한 쪽) 위치를 맞바꾼다
        // (안으로 깎을 때 방향이 자연스럽도록. 끄면 다시 원복된다).
        (_selectedBubbleTail.StartX, _selectedBubbleTail.X) = (_selectedBubbleTail.X, _selectedBubbleTail.StartX);
        (_selectedBubbleTail.StartY, _selectedBubbleTail.Y) = (_selectedBubbleTail.Y, _selectedBubbleTail.StartY);

        UpdateBubbleGeometry(_selectedBubble); // 도형 + 꼬리 핸들 위치 갱신.
        UpdateBubbleTailList(_selectedBubble);
        BubbleTailListBox.SelectedItem = _selectedBubbleTail;
        UpdateStatus(_selectedBubbleTail.TailInward ? "이 꼬리를 안으로 깎습니다(시작↔끝 교체)." : "이 꼬리를 밖으로 냅니다.");
    }

    private static readonly (string Name, string Hex)[] ColorPalette =
    {
        ("검정", "#000000"), ("흰색", "#FFFFFF"), ("회색", "#868E96"),
        ("빨강", "#E03131"), ("주황", "#F08C00"), ("노랑", "#F2CC0C"),
        ("초록", "#2F9E44"), ("파랑", "#1971C2"), ("보라", "#9C36B5"),
    };

    private void PopulateColorCombo(ComboBox combo, string defaultHex)
    {
        foreach (var (name, hex) in ColorPalette)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 0)
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatch);
            panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            combo.Items.Add(new ComboBoxItem { Content = panel, Tag = hex });
        }

        SelectComboColor(combo, defaultHex);
    }

    private static void SelectComboColor(ComboBox combo, string hex)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is string tag && string.Equals(tag, hex, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = -1;
    }

    private static Color GetComboColor(ComboBox combo, Color fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        return fallback;
    }

    private static string ToHex(Brush? brush)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Black;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static Color ParseColorOr(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return fallback;
        }
    }

    private void BubbleFillColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.TextBlock.Fill = new SolidColorBrush(GetComboColor(BubbleFillColorComboBox, Colors.Black));
        // 집중선/효과선은 선 색이 글자색을 따르므로 즉시 갱신한다.
        UpdateBubbleShapePath(_selectedBubble);
    }

    private void BubbleStrokeColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.TextBlock.Stroke = new SolidColorBrush(GetComboColor(BubbleStrokeColorComboBox, Colors.White));
    }

    private void BubbleBackgroundColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.BackgroundBrush = new SolidColorBrush(GetComboColor(BubbleBackgroundColorComboBox, Colors.White));
        _selectedBubble.ShapePath.Fill = _selectedBubble.BackgroundBrush;
    }

    private void ImageCropCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedImage == null)
        {
            return;
        }

        var cropped = ImageCropCheckBox.IsChecked == true;
        SetImageCrop(_selectedImage, cropped);
        UpdateStatus(cropped ? "이미지를 칸 안에서 자릅니다." : "이미지가 칸 밖으로도 보입니다.");
    }

    private void BubbleShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.Shape = GetSelectedBubbleShape();
        UpdateBubbleGeometry(_selectedBubble);
    }

    private void BubbleShapeStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleShapeStrengthText != null)
        {
            BubbleShapeStrengthText.Text = $"강도: {BubbleShapeStrengthSlider.Value:0}";
        }

        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.ShapeStrength = BubbleShapeStrengthSlider.Value;
        UpdateBubbleGeometry(_selectedBubble);
    }

    private void BubbleShapeCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleShapeCountText != null)
        {
            BubbleShapeCountText.Text = $"돌기: {BubbleShapeCountSlider.Value:0}";
        }

        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.ShapeCount = (int)Math.Round(BubbleShapeCountSlider.Value);
        UpdateBubbleGeometry(_selectedBubble);
    }

    private void BubbleTailSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingInspector || _selectedBubble == null || _selectedBubbleTail == null)
        {
            UpdateInspectorLabels();
            return;
        }

        _selectedBubbleTail.X = BubbleTailXSlider.Value;
        _selectedBubbleTail.Y = BubbleTailYSlider.Value;
        _selectedBubbleTail.Width = BubbleTailWidthSlider.Value;
        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleTailHandles(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        BubbleTailListBox.SelectedItem = _selectedBubbleTail;
        UpdateInspectorLabels();
    }

    private void SelectedBubbleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.TextBlock.Text = SelectedBubbleTextBox.Text;
    }

    private void CreateLayoutFromPattern(string patternText)
    {
        var pattern = ParsePattern(patternText);
        if (pattern.Count == 0)
        {
            UpdateStatus("칸 구성은 1,2,1 처럼 숫자와 쉼표로 입력하세요.");
            return;
        }

        var margin = Math.Max(0, ParseDoubleOr(AutoMarginTextBox.Text, 24));
        var gutter = Math.Max(0, ParseDoubleOr(AutoGutterTextBox.Text, 14));
        // 페이지 높이를 줄 수만큼 꽉 채운다(상한 없음).
        var rowHeight = Math.Max(20, (_pageHeight - margin * 2 - gutter * (pattern.Count - 1)) / pattern.Count);

        // 격자 슬롯(위치/크기) 목록을 먼저 계산한다.
        var slots = new List<Rect>();
        var y = margin;
        foreach (var columns in pattern)
        {
            var panelWidth = Math.Max(20, (_pageWidth - margin * 2 - gutter * (columns - 1)) / columns);
            var x = margin;
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new Rect(x, y, panelWidth, rowHeight));
                x += panelWidth + gutter;
            }

            y += rowHeight + gutter;
        }

        // 기존 칸은 순서대로 슬롯에 재배치(내용 유지). 슬롯이 더 많으면 빈 칸을 추가하고,
        // 기존 칸이 더 많을 때만 초과분을 삭제한다.
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (i < _panels.Count)
            {
                ApplyPanelBounds(_panels[i], slot.X, slot.Y, slot.Width, slot.Height);
            }
            else
            {
                AddPanel(CreatePanel(_nextPanelNumber++, slot.X, slot.Y, slot.Width, slot.Height));
            }
        }

        while (_panels.Count > slots.Count)
        {
            RemovePanel(_panels[^1]);
        }

        RenumberPanels();
        UpdatePanelOrder();
        UpdatePanelList();
        UpdateMergedBubbleOutlines();
        ClearSelection();
        UpdateLayoutSummary();
        UpdateStatus("기본 칸 구성을 적용했습니다.");
    }

    private void SaveCurrentPageState()
    {
        if (_pages.Count == 0 || _currentPageIndex < 0 || _currentPageIndex >= _pages.Count)
        {
            return;
        }

        _pages[_currentPageIndex] = CaptureCurrentPage(_pages[_currentPageIndex].Name);
    }

    private ComicPageData CaptureCurrentPage(string name)
    {
        var page = new ComicPageData
        {
            Name = name,
            PageWidth = _pageWidth,
            PageHeight = _pageHeight,
            BlackBackground = BlackBackgroundCheckBox?.IsChecked == true
        };

        foreach (var panel in _panels)
        {
            var panelData = new ComicPanelData
            {
                Number = panel.Number,
                X = GetCanvasLeft(panel.Frame),
                Y = GetCanvasTop(panel.Frame),
                Width = panel.Frame.Width,
                Height = panel.Frame.Height,
                IsLocked = panel.IsLocked,
                CornerMode = panel.CornerMode,
                CornerOffsets = PanelOffsetsToArray(panel.CornerOffsets)
            };

            foreach (var image in panel.Images)
            {
                panelData.Images.Add(new PanelImageData
                {
                    Path = image.Path,
                    Scale = image.Scale.ScaleX,
                    TranslateX = image.Translate.X,
                    TranslateY = image.Translate.Y,
                    IsCropped = image.IsCropped,
                    IsLocked = image.IsLocked
                });
            }

            foreach (var bubble in panel.Bubbles)
            {
                var position = GetBubblePositionInOwnerPanel(bubble);
                panelData.Bubbles.Add(new SpeechBubbleData
                {
                    Text = bubble.TextBlock.Text,
                    X = position.X,
                    Y = position.Y,
                    Width = bubble.Container.Width,
                    Height = bubble.Container.Height,
                    FontSize = bubble.TextBlock.FontSize,
                    TextMarginLeft = bubble.TextBlock.Margin.Left,
                    TextMarginTop = bubble.TextBlock.Margin.Top,
                    TextMarginRight = bubble.TextBlock.Margin.Right,
                    TextMarginBottom = bubble.TextBlock.Margin.Bottom,
                    IsCropped = bubble.IsCropped,
                    IsLocked = bubble.IsLocked,
                    HasTextOutline = bubble.TextBlock.OutlineEnabled,
                    FillColor = ToHex(bubble.TextBlock.Fill),
                    StrokeColor = ToHex(bubble.TextBlock.Stroke),
                    BackgroundColor = ToHex(bubble.BackgroundBrush),
                    Shape = bubble.Shape.ToString(),
                    ShapeCount = bubble.ShapeCount,
                    ShapeStrength = bubble.ShapeStrength,
                    Tails = bubble.Tails
                        .Select(tail => new BubbleTailData
                        {
                            StartX = tail.StartX,
                            StartY = tail.StartY,
                            MidX = tail.MidX,
                            MidY = tail.MidY,
                            X = tail.X,
                            Y = tail.Y,
                            Width = tail.Width,
                            TailInward = tail.TailInward
                        })
                        .ToList()
                });
            }

            page.Panels.Add(panelData);
        }

        return page;
    }

    private List<ComicPageData> CaptureProjectPages(string? projectDirectory)
    {
        SaveCurrentPageState();
        var copiedPages = new List<ComicPageData>();

        foreach (var page in _pages)
        {
            var copiedPage = new ComicPageData { Name = page.Name, PageWidth = page.PageWidth, PageHeight = page.PageHeight };

            foreach (var panel in page.Panels)
            {
                var copiedPanel = new ComicPanelData
                {
                    Number = panel.Number,
                    X = panel.X,
                    Y = panel.Y,
                    Width = panel.Width,
                    Height = panel.Height,
                    IsLocked = panel.IsLocked,
                    CornerMode = panel.CornerMode,
                    CornerOffsets = (double[])panel.CornerOffsets.Clone(),
                    Bubbles = panel.Bubbles
                };

                foreach (var image in panel.Images)
                {
                    copiedPanel.Images.Add(new PanelImageData
                    {
                        Path = MakeStorablePath(image.Path, projectDirectory),
                        Scale = image.Scale,
                        TranslateX = image.TranslateX,
                        TranslateY = image.TranslateY,
                        IsCropped = image.IsCropped,
                        IsLocked = image.IsLocked
                    });
                }

                copiedPage.Panels.Add(copiedPanel);
            }

            copiedPages.Add(copiedPage);
        }

        return copiedPages;
    }

    private void LoadPage(ComicPageData page)
    {
        ClearPageVisuals();
        _nextPanelNumber = 1;
        // 페이지마다 개별 크기를 적용한다(입력칸도 갱신).
        SetPageSize(page.PageWidth, page.PageHeight, true);
        // 페이지별 배경색 적용.
        if (BlackBackgroundCheckBox != null)
        {
            BlackBackgroundCheckBox.IsChecked = page.BlackBackground;
        }

        ApplyPageBackground();

        foreach (var panelData in page.Panels)
        {
            var panel = CreatePanel(panelData.Number, panelData.X, panelData.Y, panelData.Width, panelData.Height);
            AddPanel(panel);
            _nextPanelNumber = Math.Max(_nextPanelNumber, panelData.Number + 1);

            foreach (var imageData in panelData.Images)
            {
                var imagePath = ResolveProjectPath(imageData.Path);

                if (!File.Exists(imagePath))
                {
                    continue;
                }

                var image = AddPanelImage(panel, imagePath);
                image.Scale.ScaleX = imageData.Scale <= 0 ? 1 : imageData.Scale;
                image.Scale.ScaleY = imageData.Scale <= 0 ? 1 : imageData.Scale;
                image.Translate.X = imageData.TranslateX;
                image.Translate.Y = imageData.TranslateY;
                SetImageCrop(image, imageData.IsCropped);
                SetImageLocked(image, imageData.IsLocked);
            }

            foreach (var bubbleData in panelData.Bubbles)
            {
                var bubble = CreateSpeechBubble(
                    panel,
                    bubbleData.Text,
                    bubbleData.Width,
                    bubbleData.Height,
                    bubbleData.FontSize,
                    bubbleData.X,
                    bubbleData.Y);

                bubble.TextBlock.Margin = new Thickness(bubbleData.TextMarginLeft, bubbleData.TextMarginTop, bubbleData.TextMarginRight, bubbleData.TextMarginBottom);

                var (mappedShape, legacyStrength) = MapShape(bubbleData.Shape);
                bubble.Shape = mappedShape;
                bubble.ShapeStrength = legacyStrength ?? bubbleData.ShapeStrength;
                bubble.ShapeCount = bubbleData.ShapeCount <= 0 ? 9 : bubbleData.ShapeCount;
                bubble.Tails.Clear();
                bubble.Tails.AddRange(bubbleData.Tails.Select(tail => new BubbleTail
                {
                    StartX = tail.StartX,
                    StartY = tail.StartY,
                    MidX = double.IsNaN(tail.MidX) ? (tail.StartX + tail.X) / 2 : tail.MidX,
                    MidY = double.IsNaN(tail.MidY) ? (tail.StartY + tail.Y) / 2 : tail.MidY,
                    X = tail.X,
                    Y = tail.Y,
                    Width = tail.Width,
                    // 구버전(말풍선 단위) 저장 호환: 말풍선 값이 켜져 있으면 모든 꼬리에 적용.
                    TailInward = tail.TailInward || bubbleData.TailInward
                }));
                UpdateBubbleGeometry(bubble);

                AttachBubbleToPanelOverlay(bubble);
                if (!bubbleData.IsCropped)
                {
                    SetBubbleCrop(bubble, false);
                }

                SetBubbleLocked(bubble, bubbleData.IsLocked);
                bubble.TextBlock.OutlineEnabled = bubbleData.HasTextOutline;
                bubble.TextBlock.Fill = new SolidColorBrush(ParseColorOr(bubbleData.FillColor, Colors.Black));
                bubble.TextBlock.Stroke = new SolidColorBrush(ParseColorOr(bubbleData.StrokeColor, Colors.White));
                bubble.BackgroundBrush = new SolidColorBrush(ParseColorOr(bubbleData.BackgroundColor, Colors.White));
                bubble.ShapePath.Fill = bubble.BackgroundBrush;
                panel.Bubbles.Add(bubble);
            }

            UpdateBubbleOrder(panel);
            UpdateMergedBubbleOutlines();
            panel.SelectedImage = panel.Images.LastOrDefault();
            panel.Placeholder.Visibility = Visibility.Collapsed;
            SetPanelLocked(panel, panelData.IsLocked);

            // 칸 사변형 모서리 복원.
            panel.CornerMode = panelData.CornerMode;
            ApplyArrayToPanelOffsets(panelData.CornerOffsets, panel.CornerOffsets);
            UpdatePanelShape(panel);
        }

        // 페이지를 열거나 넘어갈 때는 칸을 자동 선택하지 않고 모든 선택을 해제한다.
        ClearSelection();

        UpdateLayoutSummary();
        UpdatePageIndicator();
    }

    private void ClearPageVisuals()
    {
        // 페이지를 떠날 때 움직이는 이미지/동영상의 타이머·재생을 멈춰 자원 누수를 막는다.
        foreach (var panel in _panels)
        {
            foreach (var image in panel.Images)
            {
                image.StopPlayback();
            }
        }

        _panels.Clear();
        _selectedPanel = null;
        _selectedBubble = null;
        _selectedImage = null;
        PanelCanvas.Children.Clear();
        PageOverlay.Children.Clear();
        PanelListBox.Items.Clear();
        UpdateImageList(null);
        UpdateBubbleList(null);
    }

    private void UpdatePageList()
    {
        if (PageListBox == null)
        {
            return;
        }

        _isUpdatingPageList = true;
        PageListBox.Items.Clear();

        for (var index = 0; index < _pages.Count; index++)
        {
            PageListBox.Items.Add($"{index + 1}. {_pages[index].Name}");
        }

        PageListBox.SelectedIndex = _currentPageIndex;
        _isUpdatingPageList = false;
        UpdatePageIndicator();
    }

    private void UpdatePageIndicator()
    {
        if (PageIndicatorText == null)
        {
            return;
        }

        var current = _pages.Count == 0 ? 0 : _currentPageIndex + 1;
        PageIndicatorText.Text = $"페이지 {current} / {_pages.Count}";
    }

    private void AddPanel(ComicPanel panel)
    {
        _panels.Add(panel);
        PanelCanvas.Children.Add(panel.Frame);
        PanelListBox.Items.Add(panel);
        UpdatePanelOrder();
        UpdateLayoutSummary();
    }

    private ComicPanel CreatePanel(int number, double x, double y, double width, double height)
    {
        // 이미지별 크롭은 각 이미지 레이어의 ClipToBounds로 제어하므로 칸 컨테이너는 자르지 않는다.
        var imageCanvas = new Canvas
        {
            ClipToBounds = false,
            Background = Brushes.Transparent
        };

        var placeholder = new TextBlock
        {
            Text = $"{number}번 칸",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(116, 111, 102)),
            FontSize = 18,
            // 빈 칸의 중앙 안내 텍스트는 표시하지 않는다.
            Visibility = Visibility.Collapsed
        };

        // 크롭은 사변형 Clip으로 처리하므로 ClipToBounds 대신 Clip을 쓴다(모서리를 밖으로 밀어도 안 잘리게).
        var overlay = new Canvas
        {
            ClipToBounds = false,
            Background = Brushes.Transparent
        };
        // 말풍선 본체+꼬리 흰색 채움(말풍선 컨테이너 아래에 깔려 꼬리 안까지 채운다).
        var bubbleFillPath = CreateBubbleFillPath();
        overlay.Children.Add(bubbleFillPath);
        Panel.SetZIndex(bubbleFillPath, -1);
        var bubbleOutlinePath = CreateBubbleOutlinePath();
        overlay.Children.Add(bubbleOutlinePath);
        Panel.SetZIndex(bubbleOutlinePath, int.MaxValue - 1);

        // 크롭 OFF 말풍선용 비클리핑 오버레이(칸 안에 있어 칸의 z-순서를 따른다).
        // 배경을 두지 않아(null) 빈 영역의 클릭은 아래 레이어(크롭 ON 말풍선 등)로 통과시킨다.
        var freeOverlay = new Canvas
        {
            ClipToBounds = false
        };
        var freeBubbleFillPath = CreateBubbleFillPath();
        freeOverlay.Children.Add(freeBubbleFillPath);
        Panel.SetZIndex(freeBubbleFillPath, -1);
        var freeBubbleOutlinePath = CreateBubbleOutlinePath();
        freeOverlay.Children.Add(freeBubbleOutlinePath);
        Panel.SetZIndex(freeBubbleOutlinePath, int.MaxValue - 1);

        var resizeHandle = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Background = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 0, 5, 5),
            Visibility = Visibility.Hidden
        };

        // 칸 모양(사변형). 흰 배경 + 검은 외곽선을 직사각형 Border 대신 Path로 그린다(기본은 직사각형과 동일).
        var quadFill = new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            IsHitTestVisible = false
        };
        // 테두리는 변마다 두께를 다르게 줄 수 있도록 4개의 선으로 그린다(대각 변은 AA 번짐 보정을 위해 약간 얇게).
        var borderHost = new Canvas { ClipToBounds = false, IsHitTestVisible = false };
        var quadBorderLines = new System.Windows.Shapes.Line[4];
        for (var i = 0; i < 4; i++)
        {
            quadBorderLines[i] = new System.Windows.Shapes.Line
            {
                Stroke = Brushes.Black,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round, // 모서리에서 선이 자연스럽게 만나도록.
                StrokeEndLineCap = PenLineCap.Round
            };
            borderHost.Children.Add(quadBorderLines[i]);
        }

        // 크롭 OFF(넘치는) 이미지를 테두리보다 앞에 그리기 위한 캔버스.
        var freeImageCanvas = new Canvas { ClipToBounds = false, Background = null };

        // z-순서: 흰배경 → 크롭 이미지 → 크롭 말풍선 → 테두리 → 크롭OFF 이미지 → 크롭OFF 말풍선.
        // (크롭 ON 콘텐츠는 테두리 뒤, 크롭 OFF 콘텐츠는 테두리 앞)
        var grid = new Grid { ClipToBounds = false };
        grid.Children.Add(quadFill);
        grid.Children.Add(imageCanvas);   // 크롭 ON 이미지
        grid.Children.Add(overlay);       // 크롭 ON 말풍선 (테두리 뒤)
        grid.Children.Add(borderHost);    // 칸 테두리(4선)
        grid.Children.Add(freeImageCanvas); // 크롭 OFF 이미지 (테두리 앞)
        grid.Children.Add(freeOverlay);   // 크롭 OFF 말풍선 (테두리 앞)
        grid.Children.Add(placeholder);
        grid.Children.Add(resizeHandle);

        var frame = new Border
        {
            Width = width,
            Height = height,
            // 배경·외곽선은 quadFill/quadBorder가 담당한다. 프레임은 투명(드래그 히트테스트용).
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = grid,
            ClipToBounds = false,
            Cursor = Cursors.SizeAll,
            Tag = number
        };
        frame.AllowDrop = true;

        var panel = new ComicPanel(number, frame, imageCanvas, placeholder, overlay, bubbleFillPath, bubbleOutlinePath, freeOverlay, freeBubbleFillPath, freeBubbleOutlinePath, resizeHandle)
        {
            QuadFill = quadFill,
            QuadBorderLines = quadBorderLines,
            FreeImageCanvas = freeImageCanvas
        };
        Canvas.SetLeft(frame, ClampPanelX(x, width));
        Canvas.SetTop(frame, ClampPanelY(y, height));

        frame.PreviewMouseLeftButtonDown += (_, e) => BeginPanelDrag(panel, e);
        frame.PreviewMouseMove += (_, e) => DragPanel(panel, e);
        frame.PreviewMouseLeftButtonUp += (_, e) => EndPanelDrag(panel, e);
        frame.LostMouseCapture += (_, _) => ResetDragState();
        frame.PreviewMouseWheel += (_, e) => ZoomPanelImage(panel, e);
        frame.DragOver += (_, e) => DragOverPanel(panel, e);
        frame.Drop += (_, e) => DropImageOnPanel(panel, e);
        resizeHandle.DragStarted += (_, _) => SelectPanel(panel);
        resizeHandle.DragDelta += (_, e) => ResizePanel(panel, e);

        // 새로 생성하는 칸은 기본 고정 ON(불러오기 시에는 LoadPage에서 저장된 상태로 덮어쓴다).
        SetPanelLocked(panel, true);

        UpdatePanelShape(panel);
        return panel;
    }

    private static double[] PanelOffsetsToArray(Point[] offsets)
    {
        var result = new double[8];
        for (var i = 0; i < 4 && i < offsets.Length; i++)
        {
            result[i * 2] = offsets[i].X;
            result[i * 2 + 1] = offsets[i].Y;
        }

        return result;
    }

    private static void ApplyArrayToPanelOffsets(double[]? array, Point[] offsets)
    {
        if (array == null)
        {
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            var x = i * 2 < array.Length ? array[i * 2] : 0;
            var y = i * 2 + 1 < array.Length ? array[i * 2 + 1] : 0;
            offsets[i] = new Point(x, y);
        }
    }

    // 칸의 사변형 지오메트리(프레임 로컬 좌표). 모서리 변위 0이면 직사각형.
    private static Geometry CreatePanelQuadGeometry(ComicPanel panel)
    {
        var w = panel.Frame.Width;
        var corners = GetPanelCorners(panel);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(corners[0], true, true);
            context.LineTo(corners[1], true, false);
            context.LineTo(corners[2], true, false);
            context.LineTo(corners[3], true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    // 칸의 네 모서리(프레임 로컬): 0=TL,1=TR,2=BR,3=BL.
    private static Point[] GetPanelCorners(ComicPanel panel)
    {
        var w = panel.Frame.Width;
        var h = panel.Frame.Height;
        var o = panel.CornerOffsets;
        return new[]
        {
            new Point(0 + o[0].X, 0 + o[0].Y),
            new Point(w + o[1].X, 0 + o[1].Y),
            new Point(w + o[2].X, h + o[2].Y),
            new Point(0 + o[3].X, h + o[3].Y)
        };
    }

    // 칸 모양과 그에 따른 클리핑(배경/외곽선/크롭 이미지/크롭 말풍선)을 갱신한다.
    private void UpdatePanelShape(ComicPanel panel)
    {
        var geometry = CreatePanelQuadGeometry(panel);
        panel.QuadFill.Data = geometry;
        panel.Overlay.Clip = geometry;      // 크롭 ON 말풍선은 사변형으로 잘린다.

        // 테두리 4선을 모서리에 맞추고, 대각 변은 두께를 줄여 AA 번짐으로 두꺼워 보이는 것을 보정한다.
        var corners = GetPanelCorners(panel);
        var lines = panel.QuadBorderLines;
        if (lines.Length == 4)
        {
            SetBorderLine(lines[0], corners[0], corners[1]); // 상
            SetBorderLine(lines[1], corners[1], corners[2]); // 우
            SetBorderLine(lines[2], corners[2], corners[3]); // 하
            SetBorderLine(lines[3], corners[3], corners[0]); // 좌
        }

        foreach (var image in panel.Images)
        {
            ApplyImageClip(image, geometry);
        }
    }

    private const double PanelBorderThickness = 3.0;

    private static void SetBorderLine(System.Windows.Shapes.Line line, Point a, Point b)
    {
        line.X1 = a.X;
        line.Y1 = a.Y;
        line.X2 = b.X;
        line.Y2 = b.Y;

        var dx = Math.Abs(b.X - a.X);
        var dy = Math.Abs(b.Y - a.Y);
        var len = Math.Max(0.0001, Math.Sqrt(dx * dx + dy * dy));
        // 축 정렬도(1=수평/수직, 0.707=45°). 대각일수록 작아져 두께를 줄인다(AA 번짐 보정).
        var alignment = Math.Max(dx, dy) / len;
        line.StrokeThickness = PanelBorderThickness * alignment;
    }

    // 이미지가 크롭 ON이면 칸 사변형으로 클립, OFF면 클립 없음(칸 밖으로 넘침).
    private static void ApplyImageClip(PanelImage image, Geometry? panelGeometry)
    {
        image.Layer.Clip = image.IsCropped ? (panelGeometry ?? CreatePanelQuadGeometry(image.OwnerPanel)) : null;
    }

    private SpeechBubble CreateSpeechBubble(ComicPanel ownerPanel, string text, double width, double height, double fontSize, double x, double y)
    {
        var textBlock = new OutlinedTextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = new FontFamily("Malgun Gothic"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Fill = Brushes.Black,
            Stroke = Brushes.White,
            OutlineEnabled = false,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = DefaultBubbleTextMargin,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var resizeHandle = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Background = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 0, 5, 5),
            Visibility = Visibility.Hidden
        };

        // 본체 흰색 채움은 오버레이의 BubbleFillPath가 담당한다(꼬리 안으로 깎기가 보이도록 본체는 투명).
        // 이 BodyPath는 모양 지오메트리(데이터) 보관용이다.
        var bodyPath = new System.Windows.Shapes.Path
        {
            Fill = Brushes.Transparent,
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
            IsHitTestVisible = false
        };

        var selectionBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden
        };

        // 속도선용 선 호스트(박스로 클립). 비어 있으면 아무것도 안 그린다.
        var lineHost = new Canvas { ClipToBounds = true, IsHitTestVisible = false };

        var content = new Grid();
        content.Children.Add(bodyPath);
        content.Children.Add(lineHost);
        content.Children.Add(textBlock);
        content.Children.Add(selectionBorder);
        content.Children.Add(resizeHandle);

        var container = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = content,
            Cursor = Cursors.SizeAll,
            Tag = "SpeechBubble"
        };

        // 오버레이에 깔리는 본체+꼬리 채움/외곽선 경로(말풍선마다 독립적이라 배경색을 따로 줄 수 있다).
        var shapePath = new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };

        var bubble = new SpeechBubble(ownerPanel, container, bodyPath, shapePath, textBlock, selectionBorder, resizeHandle)
        {
            Shape = GetSelectedBubbleShape(),
            LineHost = lineHost
        };
        bubble.RelativeX = x;
        bubble.RelativeY = y;
        UpdateBubbleGeometry(bubble);
        Canvas.SetLeft(container, x);
        Canvas.SetTop(container, y);

        container.PreviewMouseLeftButtonDown += (_, e) => BeginBubbleDrag(bubble, e);
        container.PreviewMouseMove += (_, e) => DragBubble(bubble, e);
        container.PreviewMouseLeftButtonUp += (_, e) => EndBubbleDrag(bubble, e);
        container.LostMouseCapture += (_, _) => ResetDragState();
        resizeHandle.DragStarted += (_, _) => SelectBubble(bubble);
        resizeHandle.DragDelta += (_, e) => ResizeBubble(bubble, e);

        return bubble;
    }

    // 마우스를 올린 위치에서 클릭하면 선택될 대상을 작은 툴팁으로 즉시 보여준다.
    private void PageSurface_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        UpdateHoverTooltip(e.OriginalSource as DependencyObject);
    }

    // 호버 툴팁 갱신. source는 커서 아래 최상위 요소(이동 시 e.OriginalSource, 키 이벤트 시 Mouse.DirectlyOver).
    private void UpdateHoverTooltip(DependencyObject? source)
    {
        EnsureHoverPopup();

        // 뷰어 모드(인스펙터 닫힘)·페이지 밖·드래그 중에는 숨긴다(어차피 선택 불가).
        if (!IsInspectorOpen() || !PageSurface.IsMouseOver || _isDraggingPanel || _isDraggingBubble || _isDraggingPanelImage)
        {
            _hoverPopup!.IsOpen = false;
            return;
        }

        // 1) 작은 UI 핸들(꼬리·텍스트·모서리·크기 변경) 위에 있으면 그 핸들을 알려준다.
        var label = GetHoverHandleLabel(source);

        if (label == null)
        {
            // 2) 그 외에는 클릭하면 실제로 선택될 종류를 알려준다.
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            label = ctrl
                // Ctrl+클릭은 기하학적으로 고정 포함 탐색(TrySelectAtPagePoint와 동일).
                ? FindObjectAtPagePoint(Mouse.GetPosition(PanelCanvas), includeLocked: true) switch
                {
                    ComicPanel => "칸",
                    PanelImage => "이미지",
                    SpeechBubble => "말풍선",
                    _ => null
                }
                // 일반 클릭은 실제 히트테스트 라우팅(고정=통과, 픽셀 알파)을 그대로 예측.
                : PredictNormalClick(source);
        }

        if (label == null)
        {
            _hoverPopup!.IsOpen = false;
            return;
        }

        _hoverText!.Text = label;
        var pos = Mouse.GetPosition(PageScrollViewer);
        _hoverPopup!.HorizontalOffset = pos.X + 16;
        _hoverPopup!.VerticalOffset = pos.Y + 20;
        _hoverPopup!.IsOpen = true;
    }

    // 일반(비-Ctrl) 클릭이 실제로 무엇을 선택할지 예측한다.
    // source는 히트테스트로 결정된 최상위 요소(고정 오브젝트는 통과)라 실제 라우팅과 일치한다.
    private string? PredictNormalClick(DependencyObject? source)
    {
        var node = source;
        ComicPanel? framePanel = null;
        while (node != null)
        {
            // 말풍선 컨테이너(고정 말풍선은 히트테스트 통과라 여기 안 잡힘).
            if (node is Border bubbleBorder && Equals(bubbleBorder.Tag, "SpeechBubble"))
            {
                return "말풍선";
            }

            // 칸 프레임.
            if (node is Border frameBorder && frameBorder.Tag is int)
            {
                // 핫패스(매 마우스 이동)라 LINQ 할당 없이 직접 순회한다.
                foreach (var p in _panels)
                {
                    if (ReferenceEquals(p.Frame, frameBorder))
                    {
                        framePanel = p;
                        break;
                    }
                }
                break;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        if (framePanel == null)
        {
            return null;
        }

        // 칸 위에서는 BeginPanelDrag와 동일하게: 테두리(onBorder)면 이미지 검사를 건너뛰고 칸,
        // 테두리가 아니고 불투명 이미지 픽셀(고정 제외)이면 이미지, 아니면 칸.
        var local = Mouse.GetPosition(framePanel.Frame);
        if (!IsOnPanelBorder(framePanel, local) &&
            FindImageAtPoint(framePanel, local, includeLocked: false) != null)
        {
            return "이미지";
        }

        return framePanel.IsLocked ? null : "칸";
    }

    // 커서가 어떤 UI 핸들 위에 있는지 식별해 라벨을 돌려준다(없으면 null).
    private string? GetHoverHandleLabel(DependencyObject? source)
    {
        var node = source;
        while (node != null && node is not Thumb)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is not Thumb thumb)
        {
            return null;
        }

        if (thumb == _tailStartHandle) return "꼬리 시작점";
        if (thumb == _tailMidHandle) return "꼬리 중간점";
        if (thumb == _tailEndHandle) return "꼬리 끝점";

        if (thumb == _textRegionTopLeft || thumb == _textRegionTopRight ||
            thumb == _textRegionBottomLeft || thumb == _textRegionBottomRight)
        {
            return "텍스트 영역";
        }

        if (_panelCornerHandles != null && System.Array.IndexOf(_panelCornerHandles, thumb) >= 0)
        {
            return "칸 모서리";
        }

        if (thumb == _bubbleResizeHandle)
        {
            return "말풍선 크기 변경";
        }

        foreach (var panel in _panels)
        {
            if (panel.ResizeHandle == thumb)
            {
                return "칸 크기 변경";
            }
        }

        return null;
    }

    private void PageSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverPopup != null)
        {
            _hoverPopup.IsOpen = false;
        }
    }

    private void EnsureHoverPopup()
    {
        if (_hoverPopup != null)
        {
            return;
        }

        _hoverText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x22, 0x22, 0x22)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Child = _hoverText,
            IsHitTestVisible = false
        };
        _hoverPopup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            PlacementTarget = PageScrollViewer,
            Child = border
        };
    }

    // Ctrl+클릭: 고정된 오브젝트도 선택할 수 있게, 페이지 좌표에서 직접 위쪽부터 찾아 선택한다.
    private void PageSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsInspectorOpen())
        {
            return; // 뷰어 모드: 선택하지 않는다.
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return; // 일반 클릭은 기존 흐름대로.
        }

        var point = e.GetPosition(PanelCanvas);
        if (TrySelectAtPagePoint(point))
        {
            e.Handled = true; // 기존 핸들러(드래그/선택)가 다시 처리하지 않도록.
        }
    }

    // 페이지 좌표(PanelCanvas 기준)에서 위쪽 칸부터 말풍선→이미지→칸 순으로 클릭 대상을 찾는다.
    private object? FindObjectAtPagePoint(Point pagePoint, bool includeLocked)
    {
        for (var pi = _panels.Count - 1; pi >= 0; pi--)
        {
            var panel = _panels[pi];
            var local = new Point(pagePoint.X - GetCanvasLeft(panel.Frame), pagePoint.Y - GetCanvasTop(panel.Frame));

            // 말풍선(위쪽부터): 컨테이너 사각형 안이면 대상.
            for (var bi = panel.Bubbles.Count - 1; bi >= 0; bi--)
            {
                var bubble = panel.Bubbles[bi];
                if (!includeLocked && bubble.IsLocked)
                {
                    continue;
                }

                var bp = new Point(local.X - GetCanvasLeft(bubble.Container), local.Y - GetCanvasTop(bubble.Container));
                if (bp.X >= 0 && bp.Y >= 0 && bp.X <= bubble.Container.Width && bp.Y <= bubble.Container.Height)
                {
                    return bubble;
                }
            }

            // 이미지(고정 포함 옵션).
            var image = FindImageAtPoint(panel, local, includeLocked);
            if (image != null)
            {
                return image;
            }

            // 칸 자체(사각형 안).
            if ((includeLocked || !panel.IsLocked) &&
                local.X >= 0 && local.Y >= 0 && local.X <= panel.Frame.Width && local.Y <= panel.Frame.Height)
            {
                return panel;
            }
        }

        return null;
    }

    // Ctrl+클릭: 고정 포함, 페이지 좌표에서 찾은 대상을 선택한다.
    private bool TrySelectAtPagePoint(Point pagePoint)
    {
        switch (FindObjectAtPagePoint(pagePoint, includeLocked: true))
        {
            case SpeechBubble bubble:
                SelectBubble(bubble);
                ScrollInspectorToSection();
                return true;
            case PanelImage image:
                SelectImage(image);
                ScrollInspectorToSection();
                return true;
            case ComicPanel panel:
                SelectPanel(panel);
                ScrollInspectorToSection();
                return true;
            default:
                return false;
        }
    }

    private void BeginPanelDrag(ComicPanel panel, MouseButtonEventArgs e)
    {
        if (!IsInspectorOpen())
        {
            return; // 뷰어 모드: 칸/이미지 선택·이동 안 함.
        }

        // 말풍선/리사이즈 핸들 위 클릭은 각자(말풍선 자체 이벤트, 리사이즈 핸들)가 선택을 처리한다.
        // 단일 선택을 위해 여기서 칸을 함께 선택하지 않는다.
        if (IsInsideResizeHandle(e.OriginalSource as DependencyObject) || IsInsideBubble(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var point = e.GetPosition(panel.Frame);
        var onBorder = IsOnPanelBorder(panel, point);

        // 칸 고정과 무관하게, 고정되지 않은 이미지는 선택/드래그할 수 있다(각각 별개).
        if (!onBorder)
        {
            var image = FindImageAtPoint(panel, point);
            if (image != null)
            {
                SelectImage(image);
                ScrollInspectorToSection();
                _isDraggingPanelImage = true;
                _imageDragStart = point;
                panel.Frame.Cursor = Cursors.Hand;
                panel.Frame.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 빈 영역/테두리 = 칸 자체 조작. 고정된 칸은 선택·이동하지 않는다.
        if (panel.IsLocked)
        {
            return;
        }

        // 테두리든, 이미지가 없는 빈 영역이든 칸 자체를 드래그로 이동한다.
        SelectPanel(panel);
        ScrollInspectorToSection();
        _isDraggingPanel = true;
        _dragStart = point;
        panel.Frame.CaptureMouse();
        e.Handled = true;
    }

    private void DragPanel(ComicPanel panel, MouseEventArgs e)
    {
        if (_isDraggingPanelImage && e.LeftButton == MouseButtonState.Pressed)
        {
            if (panel.SelectedImage == null)
            {
                EndPanelDrag(panel);
                return;
            }

            var imagePosition = e.GetPosition(panel.Frame);
            panel.SelectedImage.Translate.X += imagePosition.X - _imageDragStart.X;
            panel.SelectedImage.Translate.Y += imagePosition.Y - _imageDragStart.Y;
            _imageDragStart = imagePosition;
            e.Handled = true;
            return;
        }

        if (!_isDraggingPanel || e.LeftButton != MouseButtonState.Pressed)
        {
            if (_isDraggingPanel || _isDraggingPanelImage)
            {
                EndPanelDrag(panel);
            }

            return;
        }

        var position = e.GetPosition(PanelCanvas);
        var x = ClampPanelX(position.X - _dragStart.X, panel.Frame.Width);
        var y = ClampPanelY(position.Y - _dragStart.Y, panel.Frame.Height);
        SetPanelPosition(panel, x, y);
        LoadPanelValues(panel);
        UpdateFreeBubblesForPanel(panel);
        PositionPanelCornerHandles();
    }

    private void EndPanelDrag(ComicPanel panel, MouseButtonEventArgs e)
    {
        EndPanelDrag(panel);
        e.Handled = true;
    }

    private void EndPanelDrag(ComicPanel panel)
    {
        _isDraggingPanel = false;
        _isDraggingPanelImage = false;
        panel.Frame.Cursor = Cursors.SizeAll;

        if (panel.Frame.IsMouseCaptured)
        {
            panel.Frame.ReleaseMouseCapture();
        }
    }

    private void ZoomPanelImage(ComicPanel panel, MouseWheelEventArgs e)
    {
        if (panel.SelectedImage == null || IsInsideBubble(e.OriginalSource as DependencyObject))
        {
            return;
        }

        SelectImage(panel.SelectedImage);
        var step = e.Delta > 0 ? 1.08 : 0.92;
        var nextScale = Math.Clamp(panel.SelectedImage.Scale.ScaleX * step, 0.3, 5.0);
        panel.SelectedImage.Scale.ScaleX = nextScale;
        panel.SelectedImage.Scale.ScaleY = nextScale;
        e.Handled = true;
    }

    private void DragOverPanel(ComicPanel panel, DragEventArgs e)
    {
        if (TryGetDroppedImagePaths(e, out _))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            SelectPanel(panel);
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropImageOnPanel(ComicPanel panel, DragEventArgs e)
    {
        if (!TryGetDroppedImagePaths(e, out var paths))
        {
            return;
        }

        // 파일 드래그&드롭은 이 창의 마우스다운을 동반하지 않으므로, 히스토리 캡처를 위해 직접 표시한다.
        _historyDirty = true;

        try
        {
            // 드롭 위치(콘텐츠 영역 좌표). 원본 100% 크기로, 이 지점을 중심으로 붙여넣는다.
            var dropPoint = e.GetPosition(panel.ImageCanvas);

            PanelImage? lastImage = null;
            foreach (var path in paths)
            {
                var added = AddPanelImage(panel, path);
                lastImage = added;
                CenterImageAtPoint(added, dropPoint);
                ApplyNativeScale(added);

                // 동영상은 열려야 원본 해상도를 알 수 있으므로 그때 100% 크기를 적용한다.
                if (added.Media != null)
                {
                    var media = added.Media;
                    RoutedEventHandler? handler = null;
                    handler = (_, _) =>
                    {
                        media.MediaOpened -= handler;
                        ApplyNativeScale(added);
                    };
                    media.MediaOpened += handler;
                }
            }

            SelectPanel(panel);
            if (lastImage != null)
            {
                SelectImage(lastImage);
            }

            UpdateStatus($"{panel.Number}번 칸에 이미지 {paths.Count}개를 드롭했습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"이미지를 열 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResizePanel(ComicPanel panel, DragDeltaEventArgs e)
    {
        SelectPanel(panel);

        // 칸이 페이지 밖으로 커질 수 있게 위쪽 한계는 두지 않는다(넘어간 부분은 잘림).
        var width = Math.Max(PanelWidthSlider.Minimum, panel.Frame.Width + e.HorizontalChange);
        var height = Math.Max(PanelHeightSlider.Minimum, panel.Frame.Height + e.VerticalChange);

        panel.Frame.Width = width;
        panel.Frame.Height = height;
        UpdatePanelImageSizes(panel);
        LoadPanelValues(panel);
        UpdateFreeBubblesForPanel(panel);
        PositionPanelCornerHandles();
    }

    private void BeginBubbleDrag(SpeechBubble bubble, MouseButtonEventArgs e)
    {
        if (!IsInspectorOpen())
        {
            return; // 뷰어 모드: 말풍선 선택·이동 안 함.
        }

        SelectBubble(bubble);
        ScrollInspectorToSection();
        if (IsInsideResizeHandle(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isDraggingBubble = true;
        _dragStart = e.GetPosition(bubble.Container);
        bubble.Container.CaptureMouse();
        e.Handled = true;
    }

    private void DragBubble(SpeechBubble bubble, MouseEventArgs e)
    {
        if (!_isDraggingBubble || e.LeftButton != MouseButtonState.Pressed)
        {
            if (_isDraggingBubble)
            {
                EndBubbleDrag(bubble);
            }

            return;
        }

        var canvas = (Canvas)bubble.Container.Parent;
        var position = e.GetPosition(canvas);
        Canvas.SetLeft(bubble.Container, position.X - _dragStart.X);
        Canvas.SetTop(bubble.Container, position.Y - _dragStart.Y);

        // 슬라이더 값은 인스펙터 표시용으로만 갱신한다. 콜백(ApplyBubbleValues→전체 재생성)이
        // 매 이동마다 중복 실행되지 않도록 _isLoadingInspector로 억제한다.
        var relative = GetBubblePositionInOwnerPanel(bubble);
        _isLoadingInspector = true;
        BubbleXSlider.Value = Math.Clamp(relative.X, BubbleXSlider.Minimum, BubbleXSlider.Maximum);
        BubbleYSlider.Value = Math.Clamp(relative.Y, BubbleYSlider.Minimum, BubbleYSlider.Maximum);
        _isLoadingInspector = false;

        UpdateMergedBubbleOutlines(bubble.OwnerPanel);
        if (bubble == _selectedBubble)
        {
            PositionSelectedTailHandles();
        }
        UpdateInspectorLabels();
    }

    private void EndBubbleDrag(SpeechBubble bubble, MouseButtonEventArgs e)
    {
        EndBubbleDrag(bubble);
        e.Handled = true;
    }

    private void EndBubbleDrag(SpeechBubble bubble)
    {
        _isDraggingBubble = false;

        if (bubble.Container.IsMouseCaptured)
        {
            bubble.Container.ReleaseMouseCapture();
        }
    }

    private void ResizeBubble(SpeechBubble bubble, DragDeltaEventArgs e)
    {
        SelectBubble(bubble);

        var width = Math.Clamp(bubble.Container.Width + e.HorizontalChange, BubbleWidthSlider.Minimum, BubbleWidthSlider.Maximum);
        var height = Math.Clamp(bubble.Container.Height + e.VerticalChange, BubbleHeightSlider.Minimum, BubbleHeightSlider.Maximum);

        bubble.Container.Width = width;
        bubble.Container.Height = height;
        BubbleWidthSlider.Value = width;
        BubbleHeightSlider.Value = height;
        UpdateBubbleGeometry(bubble);
        UpdateInspectorLabels();
        PositionBubbleSelectionBox();
    }

    // 말풍선 위에서 마우스 휠로 크기를 확대/축소(중앙 고정).
    private void ZoomBubble(SpeechBubble bubble, MouseWheelEventArgs e)
    {
        SelectBubble(bubble);

        var step = e.Delta > 0 ? 1.08 : 0.92;
        var oldWidth = bubble.Container.Width;
        var oldHeight = bubble.Container.Height;
        var width = Math.Clamp(oldWidth * step, BubbleWidthSlider.Minimum, BubbleWidthSlider.Maximum);
        var height = Math.Clamp(oldHeight * step, BubbleHeightSlider.Minimum, BubbleHeightSlider.Maximum);
        var left = GetCanvasLeft(bubble.Container) + (oldWidth - width) / 2;
        var top = GetCanvasTop(bubble.Container) + (oldHeight - height) / 2;

        bubble.Container.Width = width;
        bubble.Container.Height = height;
        Canvas.SetLeft(bubble.Container, left);
        Canvas.SetTop(bubble.Container, top);
        bubble.RelativeX = left;
        bubble.RelativeY = top;

        // 박스 크기 변화 비율에 맞춰 꼬리(시작·중간·끝점)도 함께 이동(상대 위치 유지).
        if (oldWidth > 0 && oldHeight > 0)
        {
            var ratioW = width / oldWidth;
            var ratioH = height / oldHeight;
            foreach (var tail in bubble.Tails)
            {
                tail.StartX *= ratioW;
                tail.StartY *= ratioH;
                tail.MidX *= ratioW;
                tail.MidY *= ratioH;
                tail.X *= ratioW;
                tail.Y *= ratioH;
            }
        }

        // 슬라이더 갱신이 ApplyBubbleValues를 다시 호출하지 않도록 가드한다.
        _isLoadingInspector = true;
        BubbleWidthSlider.Value = width;
        BubbleHeightSlider.Value = height;
        BubbleXSlider.Value = Math.Clamp(left, BubbleXSlider.Minimum, BubbleXSlider.Maximum);
        BubbleYSlider.Value = Math.Clamp(top, BubbleYSlider.Minimum, BubbleYSlider.Maximum);
        _isLoadingInspector = false;

        UpdateBubbleGeometry(bubble);
        UpdateInspectorLabels();
        PositionSelectedTailHandles();
        e.Handled = true;
    }

    private SpeechBubble? FindBubbleAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Border border && Equals(border.Tag, "SpeechBubble"))
            {
                foreach (var panel in _panels)
                {
                    foreach (var bubble in panel.Bubbles)
                    {
                        if (ReferenceEquals(bubble.Container, border))
                        {
                            return bubble;
                        }
                    }
                }

                return null;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void SetPanelListSelection(ComicPanel? panel)
    {
        _isUpdatingPanelList = true;
        PanelListBox.SelectedItem = panel;
        _isUpdatingPanelList = false;
    }

    private void SelectPanel(ComicPanel panel)
    {
        // 단일 선택: 칸만 선택하고 이미지·말풍선 선택은 해제한다.
        _selectionKind = SelectionKind.Panel;
        _selectedPanel = panel;
        _selectedImage = null;
        _selectedBubble = null;
        _selectedBubbleTail = null;
        SetPanelListSelection(panel);
        _isLoadingInspector = true;
        PanelLockCheckBox.IsChecked = panel.IsLocked;
        PanelCornerModeCheckBox.IsChecked = panel.CornerMode;
        _isLoadingInspector = false;
        UpdateImageList(panel);
        UpdateBubbleList(panel);
        UpdateBubbleTailList(null);
        LoadPanelValues(panel);
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
    }

    private void SelectBubble(SpeechBubble bubble)
    {
        // 단일 선택: 말풍선만 선택. 소속 칸은 리스트/인스펙터 맥락으로만 둔다(칸 선택 아님).
        _selectionKind = SelectionKind.Bubble;
        _selectedBubble = bubble;
        _selectedBubbleTail = bubble.Tails.FirstOrDefault();
        _selectedPanel = bubble.OwnerPanel;
        _selectedImage = null;
        SetPanelListSelection(null);
        UpdateImageList(bubble.OwnerPanel);
        UpdateBubbleList(bubble.OwnerPanel);
        UpdateBubbleTailList(bubble);
        BubbleListBox.SelectedItem = bubble;
        LoadPanelValues(bubble.OwnerPanel);
        LoadBubbleValues(bubble);
        UpdateBubbleTailList(bubble);
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
    }

    private void SelectBubbleTail(BubbleTail tail)
    {
        if (_selectedBubble == null || !_selectedBubble.Tails.Contains(tail))
        {
            return;
        }

        _selectedBubbleTail = tail;
        _isLoadingInspector = true;
        BubbleTailXSlider.Value = Math.Clamp(tail.X, BubbleTailXSlider.Minimum, BubbleTailXSlider.Maximum);
        BubbleTailYSlider.Value = Math.Clamp(tail.Y, BubbleTailYSlider.Minimum, BubbleTailYSlider.Maximum);
        BubbleTailWidthSlider.Value = Math.Clamp(tail.Width, BubbleTailWidthSlider.Minimum, BubbleTailWidthSlider.Maximum);
        BubbleTailInwardCheckBox.IsChecked = tail.TailInward; // '안으로 깎기'는 선택한 꼬리 개별 값.
        _isLoadingInspector = false;
        UpdateBubbleTailHandles(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        BubbleTailListBox.SelectedItem = tail;
        UpdateInspectorLabels();
    }

    private void SelectImage(PanelImage image)
    {
        // 단일 선택: 이미지만 선택. 소속 칸은 맥락으로만 둔다(칸 선택 아님).
        _selectionKind = SelectionKind.Image;
        _selectedImage = image;
        _selectedBubble = null;
        _selectedBubbleTail = null;
        _selectedPanel = image.OwnerPanel;
        image.OwnerPanel.SelectedImage = image;
        SetPanelListSelection(null);
        UpdateImageList(image.OwnerPanel);
        UpdateBubbleList(image.OwnerPanel);
        UpdateBubbleTailList(null);
        ImageListBox.SelectedItem = image;
        LoadPanelValues(image.OwnerPanel);
        _isLoadingInspector = true;
        ImageCropCheckBox.IsChecked = image.IsCropped;
        ImageLockCheckBox.IsChecked = image.IsLocked;
        _isLoadingInspector = false;
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
    }

    private void ApplyPanelValues(ComicPanel panel)
    {
        panel.Frame.Width = PanelWidthSlider.Value;
        panel.Frame.Height = PanelHeightSlider.Value;
        UpdatePanelImageSizes(panel);
        SetPanelPosition(panel, PanelXSlider.Value, PanelYSlider.Value);
        UpdateFreeBubblesForPanel(panel);
    }

    private void ApplyBubbleValues(SpeechBubble bubble)
    {
        bubble.Container.Width = BubbleWidthSlider.Value;
        bubble.Container.Height = BubbleHeightSlider.Value;
        bubble.TextBlock.FontSize = BubbleFontSlider.Value;
        UpdateBubbleGeometry(bubble);
        SetBubblePositionInOwnerPanel(bubble, BubbleXSlider.Value, BubbleYSlider.Value);
    }

    private void LoadPanelValues(ComicPanel panel)
    {
        _isLoadingInspector = true;
        PanelXSlider.Value = GetCanvasLeft(panel.Frame);
        PanelYSlider.Value = GetCanvasTop(panel.Frame);
        PanelWidthSlider.Value = panel.Frame.Width;
        PanelHeightSlider.Value = panel.Frame.Height;
        _isLoadingInspector = false;
        UpdateInspectorLabels();
    }

    private void LoadBubbleValues(SpeechBubble bubble)
    {
        _isLoadingInspector = true;
        var position = GetBubblePositionInOwnerPanel(bubble);
        SelectedBubbleTextBox.Text = bubble.TextBlock.Text;
        BubbleCropCheckBox.IsChecked = bubble.IsCropped;
        BubbleLockCheckBox.IsChecked = bubble.IsLocked;
        BubbleOutlineCheckBox.IsChecked = bubble.TextBlock.OutlineEnabled;
        SelectComboColor(BubbleFillColorComboBox, ToHex(bubble.TextBlock.Fill));
        SelectComboColor(BubbleStrokeColorComboBox, ToHex(bubble.TextBlock.Stroke));
        SelectComboColor(BubbleBackgroundColorComboBox, ToHex(bubble.BackgroundBrush));
        BubbleShapeCountSlider.Value = Math.Clamp(bubble.ShapeCount, BubbleShapeCountSlider.Minimum, BubbleShapeCountSlider.Maximum);
        BubbleShapeStrengthSlider.Value = Math.Clamp(bubble.ShapeStrength, BubbleShapeStrengthSlider.Minimum, BubbleShapeStrengthSlider.Maximum);
        // '안으로 깎기'는 선택한 꼬리 개별 값이므로, 선택된 꼬리가 있으면 그 값으로(없으면 해제).
        BubbleTailInwardCheckBox.IsChecked = _selectedBubbleTail?.TailInward == true;
        BubbleShapeComboBox.SelectedIndex = bubble.Shape switch
        {
            BubbleShape.CloudExplosion => 1,
            BubbleShape.Shout => 2,
            BubbleShape.Flash => 3,
            BubbleShape.ConcentrationLines => 4,
            BubbleShape.EffectLines => 5,
            BubbleShape.None => 6,
            _ => 0
        };
        BubbleWidthSlider.Value = bubble.Container.Width;
        BubbleHeightSlider.Value = bubble.Container.Height;
        BubbleFontSlider.Value = bubble.TextBlock.FontSize;
        BubbleXSlider.Value = Math.Clamp(position.X, BubbleXSlider.Minimum, BubbleXSlider.Maximum);
        BubbleYSlider.Value = Math.Clamp(position.Y, BubbleYSlider.Minimum, BubbleYSlider.Maximum);
        _isLoadingInspector = false;
        UpdateBubbleTailList(bubble);
        if (_selectedBubbleTail != null && bubble.Tails.Contains(_selectedBubbleTail))
        {
            SelectBubbleTail(_selectedBubbleTail);
        }
        UpdateInspectorLabels();
    }

    private void SetBubbleCrop(SpeechBubble bubble, bool isCropped)
    {
        var position = GetBubblePositionInOwnerPanel(bubble);
        RemoveBubbleFromCurrentParent(bubble);
        bubble.IsCropped = isCropped;

        if (isCropped)
        {
            AttachBubbleToPanelOverlay(bubble);
        }
        else
        {
            AttachBubbleToFreeOverlay(bubble);
        }

        SetBubblePositionInOwnerPanel(bubble, position.X, position.Y);
        UpdateBubbleOrder(bubble.OwnerPanel);
        UpdateStatus(isCropped ? "말풍선을 칸 안에서 자릅니다." : "말풍선이 종이 안에서 다른 칸 위로도 보입니다.");
    }

    // 칸 이미지 크롭 토글: 이미지 레이어의 클리핑만 켜고/끈다(끄면 칸 밖으로도 보인다).
    private void SetImageCrop(PanelImage image, bool isCropped)
    {
        image.IsCropped = isCropped;
        // 크롭은 칸 사변형 모양으로 클립한다(OFF면 클립 없이 칸 밖으로 넘침).
        ApplyImageClip(image, null);
        // 크롭 ON 이미지는 테두리 뒤(ImageCanvas), 크롭 OFF 이미지는 테두리 앞(FreeImageCanvas)에 둔다.
        MoveImageLayerToCanvas(image);
    }

    private static void MoveImageLayerToCanvas(PanelImage image)
    {
        var target = image.IsCropped ? image.OwnerPanel.ImageCanvas : image.OwnerPanel.FreeImageCanvas;
        if (ReferenceEquals(image.Layer.Parent, target))
        {
            return;
        }

        if (image.Layer.Parent is Canvas current)
        {
            current.Children.Remove(image.Layer);
        }

        target.Children.Add(image.Layer);
    }

    // 고정(잠금): 캔버스에서 클릭이 뒤로 통과해 선택되지 않게 한다(인스펙터 리스트로는 선택 가능).
    private void SetPanelLocked(ComicPanel panel, bool locked)
    {
        // 칸 자체만 잠근다(BeginPanelDrag에서 처리). 안의 이미지·말풍선은 각자 고정 상태를 따른다.
        panel.IsLocked = locked;
        if (locked && _selectedPanel == panel)
        {
            panel.ResizeHandle.Visibility = Visibility.Hidden;
        }
    }

    private void SetImageLocked(PanelImage image, bool locked)
    {
        image.IsLocked = locked;
        // 이미지 레이어 자체를 히트테스트 비활성으로 만들어 클릭이 뒤 객체(예: 침범당한 다른 칸)로 통과하게 한다.
        // 칸 안에서의 픽셀 판정(FindImageAtPoint)에서도 제외되어 칸 클릭에 영향을 주지 않는다.
        image.Layer.IsHitTestVisible = !locked;
    }

    private void SetBubbleLocked(SpeechBubble bubble, bool locked)
    {
        bubble.IsLocked = locked;
        bubble.Container.IsHitTestVisible = !locked;
    }

    private void PanelLockCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedPanel == null)
        {
            return;
        }

        SetPanelLocked(_selectedPanel, PanelLockCheckBox.IsChecked == true);
        PanelListBox.Items.Refresh();
        UpdateSelectionVisuals();
        UpdateStatus(_selectedPanel.IsLocked ? "칸을 고정했습니다." : "칸 고정을 해제했습니다.");
    }

    private void PanelCornerModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedPanel == null)
        {
            return;
        }

        _selectedPanel.CornerMode = PanelCornerModeCheckBox.IsChecked == true;
        PositionPanelCornerHandles();
        UpdateStatus(_selectedPanel.CornerMode ? "칸 모서리 조절을 켰습니다 (네 모서리를 끌어 사변형으로)." : "칸 모서리 조절을 껐습니다.");
    }

    private void ImageLockCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedImage == null)
        {
            return;
        }

        SetImageLocked(_selectedImage, ImageLockCheckBox.IsChecked == true);
        ImageListBox.Items.Refresh();
        UpdateSelectionVisuals();
        UpdateStatus(_selectedImage.IsLocked ? "이미지를 고정했습니다." : "이미지 고정을 해제했습니다.");
    }

    // Ctrl+0 / Ctrl+R 단축키: 선택 대상을 기본값으로 리셋한다.
    private void ResetSelectedToDefault()
    {
        switch (_selectionKind)
        {
            case SelectionKind.Image when _selectedImage != null:
                ResetImageToNativeTopLeft(_selectedImage); // 100% + 좌상단 스냅.
                UpdateStatus(_selectedImage.IsCropped
                    ? "이미지를 100%로 맞추고 칸 좌상단에 스냅했습니다."
                    : "이미지를 100%로 맞추고 페이지 좌상단에 스냅했습니다.");
                break;
            case SelectionKind.Panel when _selectedPanel != null:
                ResetPanelCorners(_selectedPanel); // 모서리를 기본 사각형으로.
                UpdateStatus("칸 모서리를 기본 사각형으로 되돌렸습니다.");
                break;
            case SelectionKind.Bubble when _selectedBubble != null:
                _selectedBubble.TextBlock.Margin = DefaultBubbleTextMargin; // 텍스트 여백을 기본값으로.
                PositionTextRegionHandles();
                UpdateStatus("말풍선 텍스트 여백을 기본값으로 되돌렸습니다.");
                break;
            default:
                UpdateStatus("리셋할 이미지·칸·말풍선을 먼저 선택하세요.");
                break;
        }
    }

    // 이미지를 원본 100% 크기로 맞춘 뒤 좌상단으로 스냅한다.
    // 크롭 ON이면 칸(프레임) 좌상단, OFF면 페이지 좌상단에 이미지의 좌상단을 맞춘다.
    private void ResetImageToNativeTopLeft(PanelImage image)
    {
        ApplyNativeScale(image); // 100% 크기.

        // 원본(100%) 그려지는 크기 = 소스 픽셀 크기.
        double dw = 0, dh = 0;
        if (image.Image?.Source is BitmapSource bitmap)
        {
            dw = bitmap.PixelWidth;
            dh = bitmap.PixelHeight;
        }
        else if (image.Media != null && image.Media.NaturalVideoWidth > 0)
        {
            dw = image.Media.NaturalVideoWidth;
            dh = image.Media.NaturalVideoHeight;
        }

        if (dw <= 0 || dh <= 0)
        {
            return; // 동영상 등 원본 크기를 아직 모르면 크기만 적용.
        }

        var contentW = image.Content.Width;
        var contentH = image.Content.Height;

        // RenderTransformOrigin이 중앙이므로, 그려진 이미지 중심 = (contentW/2 + tx, contentH/2 + ty).
        // 좌상단을 (0,0)에 맞추려면: tx = dw/2 - contentW/2.
        var tx = dw / 2.0 - contentW / 2.0;
        var ty = dh / 2.0 - contentH / 2.0;

        if (!image.IsCropped)
        {
            // 페이지 좌상단(0,0)은 칸 로컬 좌표로 (-frameLeft, -frameTop).
            tx -= GetCanvasLeft(image.OwnerPanel.Frame);
            ty -= GetCanvasTop(image.OwnerPanel.Frame);
        }

        image.Translate.X = tx;
        image.Translate.Y = ty;
    }

    private void ResetPanelCorners(ComicPanel panel)
    {
        for (var i = 0; i < panel.CornerOffsets.Length; i++)
        {
            panel.CornerOffsets[i] = new Point();
        }

        UpdatePanelShape(panel);
        PositionPanelCornerHandles();
    }

    // Ctrl+L 단축키: 활성 선택의 고정 상태를 토글한다. 해당 체크박스를 뒤집어 기존 핸들러(고정+목록갱신+상태표시)를 재사용한다.
    private void ToggleSelectedLock()
    {
        switch (_selectionKind)
        {
            case SelectionKind.Bubble when _selectedBubble != null:
                BubbleLockCheckBox.IsChecked = BubbleLockCheckBox.IsChecked != true;
                break;
            case SelectionKind.Image when _selectedImage != null:
                ImageLockCheckBox.IsChecked = ImageLockCheckBox.IsChecked != true;
                break;
            case SelectionKind.Panel when _selectedPanel != null:
                PanelLockCheckBox.IsChecked = PanelLockCheckBox.IsChecked != true;
                break;
            default:
                UpdateStatus("고정할 대상을 먼저 선택하세요.");
                break;
        }
    }

    private void BubbleLockCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        SetBubbleLocked(_selectedBubble, BubbleLockCheckBox.IsChecked == true);
        BubbleListBox.Items.Refresh();
        UpdateSelectionVisuals();
        UpdateStatus(_selectedBubble.IsLocked ? "말풍선을 고정했습니다." : "말풍선 고정을 해제했습니다.");
    }

    private void AttachBubbleToPanelOverlay(SpeechBubble bubble)
    {
        // 채움/외곽선 경로는 글자(컨테이너)보다 아래에 깔린다.
        bubble.OwnerPanel.Overlay.Children.Add(bubble.ShapePath);
        bubble.OwnerPanel.Overlay.Children.Add(bubble.Container);
        bubble.IsCropped = true;
    }

    // 크롭 OFF 말풍선: 칸 안의 비클리핑 오버레이에 둔다 → 칸 밖으로 넘쳐 보이되 칸의 z-순서를 따른다.
    private void AttachBubbleToFreeOverlay(SpeechBubble bubble)
    {
        bubble.OwnerPanel.FreeOverlay.Children.Add(bubble.ShapePath);
        bubble.OwnerPanel.FreeOverlay.Children.Add(bubble.Container);
        bubble.IsCropped = false;
    }

    private void RemoveBubbleFromCurrentParent(SpeechBubble bubble)
    {
        if (bubble.Container.Parent is Canvas canvas)
        {
            canvas.Children.Remove(bubble.Container);
        }

        if (bubble.ShapePath.Parent is Canvas shapeCanvas)
        {
            shapeCanvas.Children.Remove(bubble.ShapePath);
        }

        UpdateMergedBubbleOutlines();
    }

    // 두 오버레이 모두 칸 안에 있어 말풍선 위치는 항상 칸 기준 상대 좌표다(칸 이동 시 자동으로 따라옴).
    private void UpdateFreeBubblesForPanel(ComicPanel panel)
    {
    }

    private Point GetBubblePositionInOwnerPanel(SpeechBubble bubble)
    {
        var left = GetCanvasLeft(bubble.Container);
        var top = GetCanvasTop(bubble.Container);
        bubble.RelativeX = left;
        bubble.RelativeY = top;
        return new Point(left, top);
    }

    private void SetBubblePositionInOwnerPanel(SpeechBubble bubble, double x, double y)
    {
        bubble.RelativeX = x;
        bubble.RelativeY = y;
        Canvas.SetLeft(bubble.Container, x);
        Canvas.SetTop(bubble.Container, y);

        UpdateMergedBubbleOutlines();
        if (bubble == _selectedBubble)
        {
            PositionSelectedTailHandles();
        }
    }

    private void SetPanelPosition(ComicPanel panel, double x, double y)
    {
        Canvas.SetLeft(panel.Frame, ClampPanelX(x, panel.Frame.Width));
        Canvas.SetTop(panel.Frame, ClampPanelY(y, panel.Frame.Height));
        PositionSelectedTailHandles();
    }

    private void UpdateSelectionLabels()
    {
        if (_selectedBubble == null && SelectedBubbleTextBox != null)
        {
            _isLoadingInspector = true;
            SelectedBubbleTextBox.Text = string.Empty;
            BubbleCropCheckBox.IsChecked = true;
            _isLoadingInspector = false;
            _selectedBubbleTail = null;
            UpdateBubbleTailList(null);
        }
    }

    private void UpdateBubbleList(ComicPanel? panel)
    {
        if (BubbleListBox == null)
        {
            return;
        }

        _isUpdatingBubbleList = true;
        BubbleListBox.Items.Clear();

        if (panel != null)
        {
            foreach (var bubble in panel.Bubbles)
            {
                BubbleListBox.Items.Add(bubble);
            }
        }

        BubbleListBox.SelectedItem = _selectedBubble != null && _selectedBubble.OwnerPanel == panel
            ? _selectedBubble
            : null;
        _isUpdatingBubbleList = false;
    }

    private void UpdateBubbleTailList(SpeechBubble? bubble)
    {
        if (BubbleTailListBox == null)
        {
            return;
        }

        _isUpdatingBubbleTailList = true;
        BubbleTailListBox.Items.Clear();

        if (bubble != null)
        {
            foreach (var tail in bubble.Tails)
            {
                BubbleTailListBox.Items.Add(tail);
            }
        }

        BubbleTailListBox.SelectedItem = _selectedBubbleTail != null && bubble?.Tails.Contains(_selectedBubbleTail) == true
            ? _selectedBubbleTail
            : null;
        _isUpdatingBubbleTailList = false;
    }

    private bool DeleteSelectedBubbleTail()
    {
        if (_selectedBubble == null || _selectedBubbleTail == null)
        {
            return false;
        }

        _selectedBubble.Tails.Remove(_selectedBubbleTail);
        _selectedBubbleTail = _selectedBubble.Tails.FirstOrDefault();
        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        UpdateBubbleTailHandles(_selectedBubble);
        UpdateStatus("말풍선 꼬리를 삭제했습니다.");
        return true;
    }

    private void UpdateImageList(ComicPanel? panel)
    {
        if (ImageListBox == null)
        {
            return;
        }

        _isUpdatingImageList = true;
        ImageListBox.Items.Clear();

        if (panel != null)
        {
            foreach (var image in panel.Images)
            {
                ImageListBox.Items.Add(image);
            }
        }

        ImageListBox.SelectedItem = _selectedImage != null && _selectedImage.OwnerPanel == panel
            ? _selectedImage
            : null;
        _isUpdatingImageList = false;
    }

    private void ClearSelection()
    {
        _selectionKind = SelectionKind.None;
        _selectedPanel = null;
        _selectedBubble = null;
        _selectedBubbleTail = null;
        _selectedImage = null;
        SetPanelListSelection(null);
        UpdateImageList(null);
        UpdateBubbleList(null);
        UpdateBubbleTailList(null);
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
        UpdateStatus("선택을 해제했습니다.");
    }

    // 말풍선 안 텍스트의 기본 여백(생성·리셋 시). 좀 더 안쪽으로 들어가도록 키웠다.
    private static readonly Thickness DefaultBubbleTextMargin = new Thickness(32, 24, 32, 24);

    // 인스펙터 섹션 카드 기본/선택 배경·테두리.
    private static readonly Brush SectionDefaultBg = CreateFrozenBrush(0xFB, 0xF9, 0xF5);
    private static readonly Brush SectionDefaultBorder = CreateFrozenBrush(0xDA, 0xD3, 0xC6);
    private static readonly Brush SectionSelectedBg = CreateFrozenBrush(0xE3, 0xF0, 0xEE); // 옅은 틸 틴트

    // 선택 강조색: 일반은 틸, 잠긴 오브젝트는 빨강 계열로 구분한다.
    private static readonly Brush SelectionAccentBrush = CreateFrozenBrush(43, 111, 106);
    private static readonly Brush SelectionLockedBrush = CreateFrozenBrush(200, 60, 60);
    private static readonly Brush SelectionAccentTint = CreateFrozenBrush(43, 111, 106, 70);
    private static readonly Brush SelectionLockedTint = CreateFrozenBrush(200, 60, 60, 70);

    private static Brush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var panel in _panels)
        {
            // 칸 테두리(4선)는 '칸'이 활성 선택일 때만 강조한다(잠금 시 빨강). 비선택은 검정.
            var isSelectedPanel = _selectionKind == SelectionKind.Panel && panel == _selectedPanel;
            var borderBrush = isSelectedPanel
                ? (panel.IsLocked ? SelectionLockedBrush : SelectionAccentBrush)
                : Brushes.Black;
            foreach (var line in panel.QuadBorderLines)
            {
                line.Stroke = borderBrush;
            }

            panel.ResizeHandle.Visibility = isSelectedPanel && !panel.IsLocked ? Visibility.Visible : Visibility.Hidden;

            foreach (var bubble in panel.Bubbles)
            {
                bubble.BodyPath.Stroke = Brushes.Transparent;
                bubble.BodyPath.StrokeThickness = 0;
                // 선택 박스/리사이즈 핸들은 칸 경계에 잘리지 않도록 PageOverlay 싱글톤이 대신 표시한다.
                bubble.SelectionBorder.Visibility = Visibility.Hidden;
                bubble.ResizeHandle.Visibility = Visibility.Hidden;
            }

            foreach (var image in panel.Images)
            {
                // 선택된 이미지에 살짝 강조색 틴트(잠금 시 빨강)를 입힌다.
                var isSelectedImage = _selectionKind == SelectionKind.Image && image == _selectedImage;
                if (isSelectedImage)
                {
                    image.SelectionBorder.Background = image.IsLocked ? SelectionLockedTint : SelectionAccentTint;
                }

                image.SelectionBorder.Visibility = isSelectedImage ? Visibility.Visible : Visibility.Hidden;
            }
        }

        PositionSelectedTailHandles();
        UpdateInspectorSectionHighlight();
    }

    // 선택한 오브젝트 종류의 인스펙터 섹션이 보이도록 스크롤한다(캔버스/리스트 어느 쪽으로 선택하든).
    private void ScrollInspectorToSection()
    {
        Border? section = _selectionKind switch
        {
            SelectionKind.Panel => PanelSectionBorder,
            SelectionKind.Image => ImageSectionBorder,
            SelectionKind.Bubble => BubbleSectionBorder,
            _ => null
        };

        // 레이아웃이 끝난 뒤 스크롤되도록 살짝 지연 호출한다.
        var target = section;
        if (target != null)
        {
            Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), DispatcherPriority.Background);
        }
    }

    // 선택한 오브젝트 종류에 맞춰 인스펙터의 칸/이미지/말풍선 섹션 배경을 강조한다.
    private void UpdateInspectorSectionHighlight()
    {
        SetSectionHighlight(PanelSectionBorder, _selectionKind == SelectionKind.Panel);
        SetSectionHighlight(ImageSectionBorder, _selectionKind == SelectionKind.Image);
        SetSectionHighlight(BubbleSectionBorder, _selectionKind == SelectionKind.Bubble);

        // 각 섹션의 리스트 아래 옵션은 그 섹션의 오브젝트가 선택됐을 때만 표시(아니면 리스트만).
        SetVisible(PanelEditControls, _selectionKind == SelectionKind.Panel);
        SetVisible(ImageEditControls, _selectionKind == SelectionKind.Image);
        SetVisible(BubbleEditControls, _selectionKind == SelectionKind.Bubble);
    }

    private static void SetVisible(UIElement? element, bool visible)
    {
        if (element != null)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetSectionHighlight(Border? section, bool selected)
    {
        if (section == null)
        {
            return;
        }

        section.Background = selected ? SectionSelectedBg : SectionDefaultBg;
        section.BorderBrush = selected ? SelectionAccentBrush : SectionDefaultBorder;
    }

    private void UpdateInspectorLabels()
    {
        if (PanelXText == null ||
            PanelYText == null ||
            PanelWidthText == null ||
            PanelHeightText == null ||
            BubbleWidthText == null ||
            BubbleHeightText == null ||
            BubbleFontText == null ||
            BubbleXText == null ||
            BubbleYText == null ||
            BubbleTailXText == null ||
            BubbleTailYText == null ||
            BubbleTailWidthText == null)
        {
            return;
        }

        PanelXText.Text = $"칸 X 위치: {PanelXSlider.Value:0}px";
        PanelYText.Text = $"칸 Y 위치: {PanelYSlider.Value:0}px";
        PanelWidthText.Text = $"칸 너비: {PanelWidthSlider.Value:0}px";
        PanelHeightText.Text = $"칸 높이: {PanelHeightSlider.Value:0}px";
        BubbleWidthText.Text = $"말풍선 너비: {BubbleWidthSlider.Value:0}px";
        BubbleHeightText.Text = $"말풍선 높이: {BubbleHeightSlider.Value:0}px";
        BubbleFontText.Text = $"말풍선 글자: {BubbleFontSlider.Value:0}px";
        BubbleXText.Text = $"말풍선 X 위치: {BubbleXSlider.Value:0}px";
        BubbleYText.Text = $"말풍선 Y 위치: {BubbleYSlider.Value:0}px";
        BubbleTailXText.Text = $"꼬리 끝점 X: {BubbleTailXSlider.Value:0}px";
        BubbleTailYText.Text = $"꼬리 끝점 Y: {BubbleTailYSlider.Value:0}px";
        BubbleTailWidthText.Text = $"꼬리 굵기: {BubbleTailWidthSlider.Value:0}px";
    }

    private void UpdateLayoutSummary()
    {
        LayoutSummaryText.Text = $"총 {_panels.Count}칸";
    }

    private void ResetDragState()
    {
        _isDraggingPanel = false;
        _isDraggingPanelImage = false;
        _isDraggingBubble = false;

        if (_selectedPanel != null)
        {
            _selectedPanel.Frame.Cursor = Cursors.SizeAll;
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
            FrameDelays = delays
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

        var panelNumber = _selectedImage.OwnerPanel.Number;
        RemovePanelImage(_selectedImage);
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
        RemoveBubbleFromCurrentParent(_selectedBubble);
        panel.Bubbles.Remove(_selectedBubble);
        _selectedBubble = null;
        UpdateMergedBubbleOutlines();
        UpdateBubbleList(panel);
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
        UpdateStatus("말풍선을 삭제했습니다.");
        return true;
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

    private void UpdatePanelImageSizes(ComicPanel panel)
    {
        // 칸 모양은 quadBorder가 그리므로 내용 영역은 프레임 전체 크기를 쓴다.
        var width = Math.Max(0, panel.Frame.Width);
        var height = Math.Max(0, panel.Frame.Height);

        foreach (var image in panel.Images)
        {
            image.Layer.Width = width;
            image.Layer.Height = height;
            image.Content.Width = width;
            image.Content.Height = height;
            image.SelectionBorder.Width = width;
            image.SelectionBorder.Height = height;
        }

        // 크기가 바뀌면 사변형 모양/클립도 갱신한다.
        UpdatePanelShape(panel);
    }

    private void UpdateBubbleGeometry(SpeechBubble bubble)
    {
        var width = Math.Max(1, bubble.Container.Width);
        var height = Math.Max(1, bubble.Container.Height);

        bubble.BodyPath.Data = bubble.Shape switch
        {
            BubbleShape.CloudExplosion => CreateCloudExplosionGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength),
            BubbleShape.Shout => CreateShoutGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength),
            BubbleShape.Flash => CreateFlashGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength),
            BubbleShape.ConcentrationLines => CreateConcentrationLinesGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength),
            BubbleShape.EffectLines => CreateEffectLinesGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength),
            // 테두리 없음: 본체 도형 없이 글자만 보인다(채움·외곽선 모두 없음).
            BubbleShape.None => null,
            _ => CreateRoundRectGeometry(width, height, bubble.ShapeStrength)
        };

        UpdateBubbleTailHandles(bubble);
        // 이 말풍선이 속한 칸만 갱신해도 충분하다(다른 칸의 도형은 영향받지 않음).
        UpdateMergedBubbleOutlines(bubble.OwnerPanel);
    }

    // 채움은 말풍선별 ShapePath가(배경색을 따로 줄 수 있게), 외곽선은 칸 단위로 합쳐(Union) 그린다.
    // 이렇게 하면 겹친 말풍선들의 경계선이 하나로 이어져 도형이 합쳐진 것처럼 보인다.
    // only가 지정되면 그 칸만 갱신한다(드래그/슬라이더 등 한 칸만 바뀌는 경우의 비용 절감).
    private void UpdateMergedBubbleOutlines(ComicPanel? only = null)
    {
        foreach (var panel in _panels)
        {
            if (only != null && !ReferenceEquals(panel, only))
            {
                continue;
            }

            // 채움은 말풍선별 ShapePath가 담당하므로 칸 단위 채움 경로는 비운다.
            panel.BubbleFillPath.Data = null;
            panel.FreeBubbleFillPath.Data = null;

            foreach (var bubble in panel.Bubbles)
            {
                UpdateBubbleShapePath(bubble);
            }

            // 외곽선은 크롭 ON/OFF 그룹별로 합쳐 그린다(크롭 ON은 칸 오버레이라 사변형으로 클리핑됨).
            panel.BubbleOutlinePath.Data = BuildMergedBubbleOutline(panel, cropped: true);
            panel.FreeBubbleOutlinePath.Data = BuildMergedBubbleOutline(panel, cropped: false);
        }
    }

    // 한 칸의 같은 크롭 그룹 말풍선들의 본체+꼬리 도형을 모두 Union으로 합친 외곽선 도형을 만든다.
    private static Geometry? BuildMergedBubbleOutline(ComicPanel panel, bool cropped)
    {
        Geometry? merged = null;
        foreach (var bubble in panel.Bubbles)
        {
            if (bubble.IsCropped != cropped)
            {
                continue;
            }

            var geometry = BuildBubbleOverlayGeometry(bubble);
            if (geometry == null)
            {
                continue;
            }

            merged = merged == null
                ? geometry
                : Geometry.Combine(merged, geometry, GeometryCombineMode.Union, null);
        }

        return merged;
    }

    // 한 말풍선의 본체+꼬리 도형을 오버레이 좌표로 만들어 ShapePath에 적용하고 배경색을 입힌다.
    private static void UpdateBubbleShapePath(SpeechBubble bubble)
    {
        // 선 효과는 대사를 숨기고, 일반 말풍선은 보인다.
        bubble.TextBlock.Visibility = IsLineEffectShape(bubble.Shape) ? Visibility.Collapsed : Visibility.Visible;
        // 선 효과가 아닐 때는 선 호스트를 비운다.
        if (!IsLineEffectShape(bubble.Shape))
        {
            bubble.LineHost.Children.Clear();
            bubble.LineHostSignature = null;
        }

        if (bubble.BodyPath.Data == null)
        {
            // 테두리 없음: 도형 자체가 없다.
            bubble.ShapePath.Data = null;
            bubble.ShapePath.Clip = null;
            return;
        }

        // 선 효과(집중선/속도선): 선마다 개별 Path로(컨테이너 안 선 호스트) 그려 각 선이 자기 시작점부터 페이드되게 한다.
        // 선 호스트는 컨테이너 로컬 좌표라 위치 변경 시 자동으로 따라온다 → 크기/모양/돌기/강도/색이 바뀔 때만 재생성한다.
        if (IsLineEffectShape(bubble.Shape))
        {
            bubble.ShapePath.Data = null;
            bubble.ShapePath.Clip = null;

            var signature = $"{bubble.Shape}|{bubble.Container.Width:F1}|{bubble.Container.Height:F1}|{bubble.ShapeCount}|{bubble.ShapeStrength:F1}|{ToHex(bubble.TextBlock.Fill)}";
            if (signature == bubble.LineHostSignature)
            {
                return; // 위치만 바뀐 경우: 로컬 좌표라 그대로 따라오므로 재생성 불필요.
            }

            bubble.LineHostSignature = signature;
            if (bubble.Shape == BubbleShape.ConcentrationLines)
            {
                BuildConcentrationLineHost(bubble);
            }
            else
            {
                BuildEffectLineHost(bubble);
            }

            return;
        }

        // 일반 말풍선: 본체+꼬리를 합친 도형에 배경색 채움만 칠한다.
        // 외곽선(테두리)은 칸 단위 병합 경로(BubbleOutlinePath)가 그려, 겹친 말풍선의 경계선이 하나로 이어진다.
        bubble.ShapePath.Data = BuildBubbleOverlayGeometry(bubble);
        bubble.ShapePath.Fill = bubble.BackgroundBrush;
        bubble.ShapePath.Stroke = Brushes.Transparent;
        bubble.ShapePath.StrokeThickness = 0;
        bubble.ShapePath.Clip = null;
    }

    // 말풍선 본체+꼬리(안으로 깎기 포함)를 오버레이 좌표의 한 도형으로 만든다.
    // 선 효과/테두리 없음은 본체 도형이 없으므로 null.
    private static Geometry? BuildBubbleOverlayGeometry(SpeechBubble bubble)
    {
        if (bubble.BodyPath.Data == null || IsLineEffectShape(bubble.Shape))
        {
            return null;
        }

        var offset = new TranslateTransform(GetCanvasLeft(bubble.Container), GetCanvasTop(bubble.Container));
        Geometry shape = bubble.BodyPath.Data.Clone();
        shape.Transform = offset;
        foreach (var tail in bubble.Tails)
        {
            var tailGeometry = CreateTailGeometry(tail);
            tailGeometry.Transform = offset;
            var tailMode = tail.TailInward ? GeometryCombineMode.Exclude : GeometryCombineMode.Union;
            shape = Geometry.Combine(shape, tailGeometry, tailMode, null);
        }

        return shape;
    }

    private static Geometry CreateTailGeometry(BubbleTail tail)
    {
        var start = new Point(tail.StartX, tail.StartY);
        var mid = new Point(tail.MidX, tail.MidY);
        var end = new Point(tail.X, tail.Y);
        var direction = end - start;

        if (direction.Length < 1)
        {
            direction = new Vector(0, 1);
        }

        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var halfWidth = Math.Max(2, tail.Width / 2);
        var startA = start + normal * halfWidth;
        var startB = start - normal * halfWidth;

        // 중간 점을 곡선의 제어점으로 사용한다. 베이스의 양쪽 변은
        // 중간 점을 기준으로 ±halfWidth만큼 벌어진 제어점을 지나 끝점(꼭짓점)으로 모인다.
        var controlA = mid + normal * halfWidth;
        var controlB = mid - normal * halfWidth;

        var figure = new PathFigure { StartPoint = startA, IsClosed = true };
        figure.Segments.Add(new QuadraticBezierSegment(controlA, end, true));
        figure.Segments.Add(new QuadraticBezierSegment(controlB, startB, true));
        return new PathGeometry(new[] { figure });
    }

    private static Thumb CreateTailHandle(Color? color = null)
    {
        return new Thumb
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.SizeAll,
            Background = new SolidColorBrush(color ?? Color.FromRgb(43, 111, 106)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Hidden
        };
    }

    private void DragSelectedTailPoint(TailPointKind point, DragDeltaEventArgs e)
    {
        if (_selectedBubble == null || _selectedBubbleTail == null)
        {
            return;
        }

        switch (point)
        {
            case TailPointKind.Start:
                // 넓은 쪽(밑변)을 끌면 꼬리 전체를 같은 변위로 통째로 옮긴다.
                _selectedBubbleTail.StartX += e.HorizontalChange;
                _selectedBubbleTail.StartY += e.VerticalChange;
                _selectedBubbleTail.MidX += e.HorizontalChange;
                _selectedBubbleTail.MidY += e.VerticalChange;
                _selectedBubbleTail.X += e.HorizontalChange;
                _selectedBubbleTail.Y += e.VerticalChange;
                break;
            case TailPointKind.Mid:
                _selectedBubbleTail.MidX += e.HorizontalChange;
                _selectedBubbleTail.MidY += e.VerticalChange;
                break;
            default:
                _selectedBubbleTail.X += e.HorizontalChange;
                _selectedBubbleTail.Y += e.VerticalChange;
                break;
        }

        // 끝점 좌표가 바뀌는 경우(끝점 이동, 또는 시작점 이동으로 전체가 따라온 경우) 끝점 슬라이더를 동기화한다.
        if (point == TailPointKind.Start || point == TailPointKind.End)
        {
            _isLoadingInspector = true;
            BubbleTailXSlider.Value = Math.Clamp(_selectedBubbleTail.X, BubbleTailXSlider.Minimum, BubbleTailXSlider.Maximum);
            BubbleTailYSlider.Value = Math.Clamp(_selectedBubbleTail.Y, BubbleTailYSlider.Minimum, BubbleTailYSlider.Maximum);
            BubbleTailWidthSlider.Value = Math.Clamp(_selectedBubbleTail.Width, BubbleTailWidthSlider.Minimum, BubbleTailWidthSlider.Maximum);
            _isLoadingInspector = false;
        }

        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        UpdateInspectorLabels();
    }

    // 호출부 호환용: 인자 말풍선과 무관하게 선택된 꼬리에 대해 싱글톤 핸들을 갱신한다.
    private void UpdateBubbleTailHandles(SpeechBubble bubble)
    {
        PositionSelectedTailHandles();
    }

    private void PositionSelectedTailHandles()
    {
        PositionBubbleSelectionBox();
        PositionTextRegionHandles();
        PositionPanelCornerHandles();
        EnsureTailHandles();

        var tail = _selectedBubbleTail;
        var show = _selectedBubble != null && tail != null && _selectedBubble.Tails.Contains(tail);
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        _tailStartHandle!.Visibility = visibility;
        _tailMidHandle!.Visibility = visibility;
        _tailEndHandle!.Visibility = visibility;

        if (!show || _selectedBubble == null || tail == null)
        {
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        PlaceTailHandle(_tailStartHandle, origin.X + tail.StartX, origin.Y + tail.StartY);
        PlaceTailHandle(_tailMidHandle, origin.X + tail.MidX, origin.Y + tail.MidY);
        PlaceTailHandle(_tailEndHandle, origin.X + tail.X, origin.Y + tail.Y);
    }

    private static void PlaceTailHandle(Thumb handle, double pageX, double pageY)
    {
        Canvas.SetLeft(handle, pageX - handle.Width / 2);
        Canvas.SetTop(handle, pageY - handle.Height / 2);
    }

    // 선택 박스/리사이즈 핸들은 PageOverlay(비클리핑)에 두어 칸 경계를 넘는 말풍선도 잘리지 않고 보인다.
    private void EnsureBubbleSelectionUi()
    {
        if (_bubbleSelectionBox == null)
        {
            _bubbleSelectionBox = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };

            _bubbleResizeHandle = new Thumb
            {
                Width = 18,
                Height = 18,
                Cursor = Cursors.SizeNWSE,
                Background = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };
            _bubbleResizeHandle.DragStarted += (_, _) =>
            {
                if (_selectedBubble != null)
                {
                    SelectBubble(_selectedBubble);
                }
            };
            _bubbleResizeHandle.DragDelta += (_, e) =>
            {
                if (_selectedBubble != null)
                {
                    ResizeBubble(_selectedBubble, e);
                }
            };
        }

        if (!PageOverlay.Children.Contains(_bubbleSelectionBox))
        {
            PageOverlay.Children.Add(_bubbleSelectionBox);
            Panel.SetZIndex(_bubbleSelectionBox, int.MaxValue - 2);
        }

        if (!PageOverlay.Children.Contains(_bubbleResizeHandle))
        {
            PageOverlay.Children.Add(_bubbleResizeHandle!);
            Panel.SetZIndex(_bubbleResizeHandle!, int.MaxValue - 1);
        }
    }

    private void PositionBubbleSelectionBox()
    {
        EnsureBubbleSelectionUi();

        var show = _selectionKind == SelectionKind.Bubble && _selectedBubble != null;
        _bubbleSelectionBox!.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        _bubbleResizeHandle!.Visibility = show ? Visibility.Visible : Visibility.Hidden;

        if (!show || _selectedBubble == null)
        {
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;

        // 잠긴 말풍선은 선택 박스/핸들을 빨강 계열로 구분한다.
        var accent = _selectedBubble.IsLocked ? SelectionLockedBrush : SelectionAccentBrush;
        _bubbleSelectionBox.BorderBrush = accent;
        _bubbleResizeHandle.Background = accent;

        Canvas.SetLeft(_bubbleSelectionBox, origin.X);
        Canvas.SetTop(_bubbleSelectionBox, origin.Y);
        _bubbleSelectionBox.Width = w;
        _bubbleSelectionBox.Height = h;

        // 핸들은 박스 우하단 안쪽 모서리에 둔다.
        Canvas.SetLeft(_bubbleResizeHandle, origin.X + w - _bubbleResizeHandle.Width);
        Canvas.SetTop(_bubbleResizeHandle, origin.Y + h - _bubbleResizeHandle.Height);
    }

    private void EnsureTextRegionHandles()
    {
        if (_textRegionTopLeft == null)
        {
            // 텍스트 영역 모서리 핸들(선택 박스의 틸과 구분되도록 주황색).
            var color = Color.FromRgb(214, 122, 32);
            _textRegionTopLeft = CreateCornerHandle(color, Cursors.SizeNWSE);
            _textRegionTopRight = CreateCornerHandle(color, Cursors.SizeNESW);
            _textRegionBottomLeft = CreateCornerHandle(color, Cursors.SizeNESW);
            _textRegionBottomRight = CreateCornerHandle(color, Cursors.SizeNWSE);
            _textRegionTopLeft.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.TopLeft, e);
            _textRegionTopRight!.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.TopRight, e);
            _textRegionBottomLeft!.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.BottomLeft, e);
            _textRegionBottomRight!.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.BottomRight, e);
        }

        foreach (var handle in new[] { _textRegionTopLeft!, _textRegionTopRight!, _textRegionBottomLeft!, _textRegionBottomRight! })
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    private static Thumb CreateCornerHandle(Color color, Cursor cursor)
    {
        return new Thumb
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = cursor,
            Background = new SolidColorBrush(color),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Hidden
        };
    }

    private void PositionTextRegionHandles()
    {
        EnsureTextRegionHandles();

        var show = _selectionKind == SelectionKind.Bubble && _selectedBubble != null;
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        _textRegionTopLeft!.Visibility = visibility;
        _textRegionTopRight!.Visibility = visibility;
        _textRegionBottomLeft!.Visibility = visibility;
        _textRegionBottomRight!.Visibility = visibility;

        if (!show || _selectedBubble == null)
        {
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;
        var m = _selectedBubble.TextBlock.Margin;

        // 텍스트 영역 사각형(컨테이너 - 여백)의 네 모서리.
        var leftX = origin.X + m.Left;
        var rightX = origin.X + w - m.Right;
        var topY = origin.Y + m.Top;
        var bottomY = origin.Y + h - m.Bottom;

        PlaceTailHandle(_textRegionTopLeft, leftX, topY);
        PlaceTailHandle(_textRegionTopRight, rightX, topY);
        PlaceTailHandle(_textRegionBottomLeft, leftX, bottomY);
        PlaceTailHandle(_textRegionBottomRight, rightX, bottomY);
    }

    private void DragTextRegionCorner(TextRegionCorner corner, DragDeltaEventArgs e)
    {
        if (_selectedBubble == null)
        {
            return;
        }

        var tb = _selectedBubble.TextBlock;
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;
        double left = tb.Margin.Left, top = tb.Margin.Top, right = tb.Margin.Right, bottom = tb.Margin.Bottom;

        switch (corner)
        {
            case TextRegionCorner.TopLeft:
                left += e.HorizontalChange;
                top += e.VerticalChange;
                break;
            case TextRegionCorner.TopRight:
                right -= e.HorizontalChange;
                top += e.VerticalChange;
                break;
            case TextRegionCorner.BottomLeft:
                left += e.HorizontalChange;
                bottom -= e.VerticalChange;
                break;
            default:
                right -= e.HorizontalChange;
                bottom -= e.VerticalChange;
                break;
        }

        // 여백은 0 이상, 텍스트 영역이 최소 10px는 남도록 제한.
        const double minRegion = 10;
        left = Math.Clamp(left, 0, Math.Max(0, w - right - minRegion));
        right = Math.Clamp(right, 0, Math.Max(0, w - left - minRegion));
        top = Math.Clamp(top, 0, Math.Max(0, h - bottom - minRegion));
        bottom = Math.Clamp(bottom, 0, Math.Max(0, h - top - minRegion));

        tb.Margin = new Thickness(left, top, right, bottom);
        PositionTextRegionHandles();
    }

    private void EnsurePanelCornerHandles()
    {
        if (_panelCornerHandles == null)
        {
            var color = Color.FromRgb(43, 111, 106);
            _panelCornerHandles = new[]
            {
                CreateCornerHandle(color, Cursors.SizeNWSE), // TL
                CreateCornerHandle(color, Cursors.SizeNESW), // TR
                CreateCornerHandle(color, Cursors.SizeNWSE), // BR
                CreateCornerHandle(color, Cursors.SizeNESW)  // BL
            };
            for (var i = 0; i < 4; i++)
            {
                var index = i;
                _panelCornerHandles[i].DragDelta += (_, e) => DragPanelCorner(index, e);
            }
        }

        foreach (var handle in _panelCornerHandles)
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    private void PositionPanelCornerHandles()
    {
        EnsurePanelCornerHandles();

        var show = _selectionKind == SelectionKind.Panel && _selectedPanel != null && _selectedPanel.CornerMode;
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        foreach (var handle in _panelCornerHandles!)
        {
            handle.Visibility = visibility;
        }

        if (!show || _selectedPanel == null)
        {
            return;
        }

        var w = _selectedPanel.Frame.Width;
        var h = _selectedPanel.Frame.Height;
        var ox = GetCanvasLeft(_selectedPanel.Frame);
        var oy = GetCanvasTop(_selectedPanel.Frame);
        var o = _selectedPanel.CornerOffsets;

        // TL,TR,BR,BL (CornerOffsets 순서와 동일)
        PlaceTailHandle(_panelCornerHandles[0], ox + 0 + o[0].X, oy + 0 + o[0].Y);
        PlaceTailHandle(_panelCornerHandles[1], ox + w + o[1].X, oy + 0 + o[1].Y);
        PlaceTailHandle(_panelCornerHandles[2], ox + w + o[2].X, oy + h + o[2].Y);
        PlaceTailHandle(_panelCornerHandles[3], ox + 0 + o[3].X, oy + h + o[3].Y);
    }

    private void DragPanelCorner(int index, DragDeltaEventArgs e)
    {
        if (_selectedPanel == null || index < 0 || index > 3)
        {
            return;
        }

        var o = _selectedPanel.CornerOffsets;
        o[index] = new Point(o[index].X + e.HorizontalChange, o[index].Y + e.VerticalChange);

        UpdatePanelShape(_selectedPanel);
        PositionPanelCornerHandles();
    }

    // 말풍선 컨테이너의 (0,0)을 페이지(PageOverlay) 좌표로 변환한다.
    // 크롭된 말풍선은 칸 오버레이에 들어 있으므로 칸 오버레이 원점을 더해 준다.
    private Point GetBubblePageOrigin(SpeechBubble bubble)
    {
        // 말풍선은 (크롭 여부와 무관하게) 칸 안의 오버레이에 있으므로, 그 오버레이 원점을 페이지 좌표로 변환한다.
        var overlay = bubble.IsCropped ? bubble.OwnerPanel.Overlay : bubble.OwnerPanel.FreeOverlay;
        var panelOrigin = overlay.TransformToVisual(PageOverlay).Transform(new Point(0, 0));
        return new Point(panelOrigin.X + GetCanvasLeft(bubble.Container), panelOrigin.Y + GetCanvasTop(bubble.Container));
    }

    private static System.Windows.Shapes.Path CreateBubbleOutlinePath()
    {
        return new System.Windows.Shapes.Path
        {
            Fill = Brushes.Transparent,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
    }

    private static System.Windows.Shapes.Path CreateBubbleFillPath()
    {
        return new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
            IsHitTestVisible = false
        };
    }

    private void EnsureTailHandles()
    {
        if (_tailStartHandle == null)
        {
            _tailStartHandle = CreateTailHandle();
            _tailMidHandle = CreateTailHandle(Color.FromRgb(214, 122, 32));
            _tailEndHandle = CreateTailHandle();
            _tailStartHandle.DragDelta += (_, e) => DragSelectedTailPoint(TailPointKind.Start, e);
            _tailMidHandle!.DragDelta += (_, e) => DragSelectedTailPoint(TailPointKind.Mid, e);
            _tailEndHandle!.DragDelta += (_, e) => DragSelectedTailPoint(TailPointKind.End, e);
        }

        // 핸들은 페이지 좌표로 배치하므로 페이지 레이어에 올려, 칸 경계 클리핑을 받지 않게 한다.
        foreach (var handle in new[] { _tailStartHandle!, _tailMidHandle!, _tailEndHandle! })
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                handle.HorizontalAlignment = HorizontalAlignment.Left;
                handle.VerticalAlignment = VerticalAlignment.Top;
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue);
            }
        }
    }

    private BubbleShape GetSelectedBubbleShape()
    {
        if (BubbleShapeComboBox?.SelectedItem is not ComboBoxItem item)
        {
            return BubbleShape.RoundRect;
        }

        return item.Tag?.ToString() switch
        {
            "CloudExplosion" => BubbleShape.CloudExplosion,
            "Shout" => BubbleShape.Shout,
            "Flash" => BubbleShape.Flash,
            "ConcentrationLines" => BubbleShape.ConcentrationLines,
            "EffectLines" => BubbleShape.EffectLines,
            "None" => BubbleShape.None,
            _ => BubbleShape.RoundRect
        };
    }

    // 저장된 모양 문자열을 현재 모양으로 변환한다. 구버전 값은 적절한 강도로 매핑한다.
    private static (BubbleShape Shape, double? LegacyStrength) MapShape(string? raw)
    {
        return raw switch
        {
            "CloudExplosion" => (BubbleShape.CloudExplosion, null),
            "Shout" => (BubbleShape.Shout, null),
            "Flash" => (BubbleShape.Flash, null),
            "ConcentrationLines" => (BubbleShape.ConcentrationLines, null),
            "EffectLines" => (BubbleShape.EffectLines, null),
            "None" => (BubbleShape.None, null),
            "Oval" => (BubbleShape.RoundRect, 0.0),
            "Rectangle" => (BubbleShape.RoundRect, 100.0),
            "Cloud" => (BubbleShape.CloudExplosion, 0.0),
            "Explosion" => (BubbleShape.CloudExplosion, 100.0),
            _ => (BubbleShape.RoundRect, null)
        };
    }

    // 원형/사각: 강도 0이면 모서리 반지름이 절반(=타원), 100이면 0(=사각), 중간은 둥근 사각형.
    private static Geometry CreateRoundRectGeometry(double width, double height, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var rx = width / 2.0 * (1.0 - t);
        var ry = height / 2.0 * (1.0 - t);
        var geometry = new RectangleGeometry(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)), rx, ry);
        geometry.Freeze();
        return geometry;
    }

    // 구름/폭발: 강도 0이면 완전 볼록(구름), 100이면 완전 오목(폭발). 부드러운 곡선.
    private static Geometry CreateCloudExplosionGeometry(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var baseRadiusFactor = 0.6 + 0.35 * t;   // 볼록(작은 베이스) → 오목(큰 베이스)
        var pushFactor = 1.1 - 2.2 * t;           // 바깥(+1.1) → 안쪽 깊게(-1.1)
        return CreateLobedGeometry(width, height, count, baseRadiusFactor, pushFactor, false);
    }

    // 외침: 구름/폭발과 같은 방식이되 각진(직선) 모양. 강도가 클수록 변이 안쪽으로 더 깊이 패여(오목) 폭발하듯 보인다.
    private static Geometry CreateShoutGeometry(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        // 0이면 강하게 볼록(바깥으로 뾰족), 강도가 커질수록 줄어들어 오목으로 넘어간다.
        var baseRadiusFactor = 0.6 + 0.35 * t;
        var pushFactor = 1.8 - 3.4 * t;   // +1.8(강한 볼록) → -1.6(오목)
        return CreateLobedGeometry(width, height, count, baseRadiusFactor, pushFactor, true);
    }

    // 플래시(충격) 말풍선: 타원 코어 둘레에 가는 방사형 가시가 촘촘히 뻗어 나온 모양.
    // 돌기 수가 가시 밀도(내부적으로 ×5), 강도가 가시 길이를 정한다.
    private static Geometry CreateFlashGeometry(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var spikes = Math.Max(8, count * 10);  // 촘촘한 가시를 위해 돌기 수를 곱한다.
        var cx = width / 2.0;
        var cy = height / 2.0;
        var hx = width / 2.0;
        var hy = height / 2.0;
        const double coreFactor = 0.60;        // 안쪽 타원(가시 골) 반지름 비율
        var reach = 0.74 + 0.26 * t;           // 가시 끝(피크) 반지름 비율: 강도↑ = 더 길게
        var start = -Math.PI / 2.0;

        Point P(double angle, double factor) => new Point(
            cx + hx * factor * Math.Cos(angle),
            cy + hy * factor * Math.Sin(angle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(start, coreFactor), true, true);
            for (var i = 0; i < spikes; i++)
            {
                var peakAngle = start + (i + 0.5) * 2.0 * Math.PI / spikes;
                var nextValley = start + (i + 1) * 2.0 * Math.PI / spikes;
                // 가시 길이를 아주 살짝만 흔들어 거의 균일하게 한다.
                var wobble = 0.96 + 0.04 * Math.Abs(Math.Sin(i * 2.7 + 0.5));
                context.LineTo(P(peakAngle, reach * wobble), true, false);
                context.LineTo(P(nextValley, coreFactor), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 선 효과(집중선/효과선)인지. 이런 모양은 채움이 아니라 열린 선(스트로크)으로 그리며 꼬리를 합치지 않는다.
    private static bool IsLineEffectShape(BubbleShape shape) =>
        shape == BubbleShape.ConcentrationLines || shape == BubbleShape.EffectLines;

    // 결정적 의사난수(0~1). 다시 그려도 같은 모양이 나오도록 index 기반으로 만든다.
    private static double Pseudo(double seed)
    {
        var v = Math.Sin(seed * 12.9898) * 43758.5453;
        return v - Math.Floor(v);
    }

    // 시작점(start)은 투명, 진행 방향(end)으로 갈수록 불투명한 선형 그라데이션. 처음 1/6 구간은 투명을 유지.
    private static Brush CreateDirectionFadeBrush(Brush lineBrush, Point start, Point end)
    {
        var color = (lineBrush as SolidColorBrush)?.Color ?? Colors.Black;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = start,
            EndPoint = end
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0.0));      // 시작: 투명
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0 / 6));  // 1/6까지 투명 유지
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 1.0));    // 끝: 불투명
        brush.Freeze();
        return brush;
    }

    // 집중선: 중앙을 향하는 방사형 직선들. 강도가 클수록 선이 중앙에서 멀어진다. 돌기 ×10.
    // (Inner = 중앙 쪽 시작점, Edge = 바깥 끝점) 목록을 만든다.
    private static List<(Point Inner, Point Edge)> ConcentrationLineEndpoints(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var lines = Math.Max(8, count * 10);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var hx = width / 2.0;
        var hy = height / 2.0;
        const double outer = 1.0;          // 선 끝은 박스(타원) 가장자리까지.
        var innerBase = 0.62 * t;          // 강도↑ = 시작점이 중앙에서 멀어진다.
        var variationAmp = innerBase * 0.6; // 멀어질수록(거리=강도에 비례) 시작 반지름이 들쭉날쭉.
        var start = -Math.PI / 2.0;
        var step = 2.0 * Math.PI / lines;

        var result = new List<(Point, Point)>(lines);
        for (var i = 0; i < lines; i++)
        {
            // 각도 간격을 불규칙하게 흔들어 선 사이 간격이 일정하지 않게 한다.
            var angle = start + i * step + (Pseudo(i * 1.7 + 0.2) - 0.5) * step * 1.4;
            // 강도 0이면 모두 중앙에서 시작(균일), 강도↑이면 멀어지며 시작 반지름이 들쭉날쭉.
            var innerF = Math.Clamp(innerBase + (Pseudo(i + 0.3) - 0.5) * 2.0 * variationAmp, 0.0, 0.95);
            var inner = new Point(cx + hx * innerF * Math.Cos(angle), cy + hy * innerF * Math.Sin(angle));
            var edge = new Point(cx + hx * outer * Math.Cos(angle), cy + hy * outer * Math.Sin(angle));
            result.Add((inner, edge));
        }

        return result;
    }

    // BodyPath.Data용(테두리 없음 판정 등). 실제 그리기는 선마다 BuildConcentrationLineHost에서 한다.
    private static Geometry CreateConcentrationLinesGeometry(double width, double height, int count, double strength)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (inner, edge) in ConcentrationLineEndpoints(width, height, count, strength))
            {
                context.BeginFigure(inner, false, false);
                context.LineTo(edge, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 집중선을 선마다 개별 Path로 만들어 선 호스트에 채운다.
    // 각 선은 안쪽(중앙 쪽, 투명 1/6) → 바깥(불투명)으로 자기 길이 기준 그라데이션을 갖는다.
    private static void BuildConcentrationLineHost(SpeechBubble bubble)
    {
        var host = bubble.LineHost;
        host.Children.Clear();

        var w = bubble.Container.Width;
        var h = bubble.Container.Height;
        var lineColor = bubble.TextBlock.Fill;

        foreach (var (inner, edge) in ConcentrationLineEndpoints(w, h, bubble.ShapeCount, bubble.ShapeStrength))
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(inner, edge),
                // 안쪽(inner) 투명 → 바깥(edge) 불투명.
                Stroke = CreateDirectionFadeBrush(lineColor, inner, edge),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            host.Children.Add(path);
        }
    }

    // 속도선(효과선): 한 방향으로 직진하는 일직선 평행선들. 강도 0~100 → 방향 0~360도.
    // 각 선의 길이가 제각각이고 선 사이 간격도 일정하지 않다. 돌기 ×10.
    // (베이스 = -d쪽 시작점, 팁 = 진행 방향 끝점) 목록을 만든다.
    private static List<(Point Base, Point Tip)> EffectLineEndpoints(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var lines = Math.Max(8, count * 10);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var angle = t * 2.0 * Math.PI;                 // 0~360도
        var dx = Math.Cos(angle);
        var dy = Math.Sin(angle);
        var px = -dy;                                  // 진행 방향의 수직(선 간격 축)
        var py = dx;

        var span = Math.Sqrt(width * width + height * height); // 박스를 항상 가로지르도록 대각선 길이
        var half = span / 2.0;
        var spacing = span / lines;

        var result = new List<(Point, Point)>(lines);
        for (var i = 0; i < lines; i++)
        {
            // 수직 위치(선 간격)를 불규칙하게 흔든다.
            var perp = -half + (i + 0.5) * spacing + (Pseudo(i * 3.1 + 0.7) - 0.5) * spacing * 1.4;
            // 길이를 들쭉날쭉하게: 한쪽 가장자리(-d)에서 시작해 임의 길이만큼 직진한다.
            var length = span * (0.30 + 0.70 * Pseudo(i * 5.3 + 0.9));
            var a0 = -half;
            var a1 = -half + length;
            var baseP = new Point(cx + dx * a0 + px * perp, cy + dy * a0 + py * perp);
            var tip = new Point(cx + dx * a1 + px * perp, cy + dy * a1 + py * perp);
            result.Add((baseP, tip));
        }

        return result;
    }

    // BodyPath.Data용(테두리 없음 판정 등). 실제 그리기는 선마다 BuildEffectLineHost에서 한다.
    private static Geometry CreateEffectLinesGeometry(double width, double height, int count, double strength)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (baseP, tip) in EffectLineEndpoints(width, height, count, strength))
            {
                context.BeginFigure(baseP, false, false);
                context.LineTo(tip, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 속도선을 선마다 개별 Path로 만들어 선 호스트에 채운다. 각 선은 박스로 클립한 '보이는 구간' 기준으로
    // 팁(앞쪽)은 투명, 베이스(뒤쪽)는 불투명하게 자기만의 그라데이션을 갖는다(선마다 시작점 기준 페이드).
    private static void BuildEffectLineHost(SpeechBubble bubble)
    {
        var host = bubble.LineHost;
        host.Children.Clear();

        var w = bubble.Container.Width;
        var h = bubble.Container.Height;
        var lineColor = bubble.TextBlock.Fill;

        foreach (var (baseP, tip) in EffectLineEndpoints(w, h, bubble.ShapeCount, bubble.ShapeStrength))
        {
            // 베이스→팁 선분을 박스로 클립해 보이는 구간만 남긴다(c0=베이스쪽, c1=팁쪽).
            if (!ClipSegmentToBox(baseP, tip, w, h, out var c0, out var c1))
            {
                continue;
            }

            var path = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(c0, c1),
                // 팁(c1) 투명 → 베이스(c0) 불투명. 보이는 구간 기준이라 선마다 자기 시작점부터 페이드된다.
                Stroke = CreateDirectionFadeBrush(lineColor, c1, c0),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            host.Children.Add(path);
        }
    }

    // 선분 p0→p1을 [0,w]×[0,h] 박스로 클립(Liang–Barsky). 보이면 true와 클립된 양 끝점을 돌려준다.
    private static bool ClipSegmentToBox(Point p0, Point p1, double w, double h, out Point c0, out Point c1)
    {
        double t0 = 0, t1 = 1;
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        c0 = p0;
        c1 = p1;

        double[] p = { -dx, dx, -dy, dy };
        double[] q = { p0.X, w - p0.X, p0.Y, h - p0.Y };

        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-9)
            {
                if (q[i] < 0)
                {
                    return false; // 박스 밖에서 평행
                }

                continue;
            }

            var r = q[i] / p[i];
            if (p[i] < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
        }

        c0 = new Point(p0.X + t0 * dx, p0.Y + t0 * dy);
        c1 = new Point(p0.X + t1 * dx, p0.Y + t1 * dy);
        return true;
    }

    // 기준 타원 둘레의 점들 사이를 바깥(볼록)/안쪽(오목)으로 휜다. angular=true면 직선(각진), false면 곡선.
    private static Geometry CreateLobedGeometry(double width, double height, int count, double baseRadiusFactor, double pushFactor, bool angular)
    {
        var n = Math.Max(3, count);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var start = -Math.PI / 2.0;
        var minDist = Math.Min(width, height) * 0.05;

        var points = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var angle = start + i * 2.0 * Math.PI / n;
            // 점마다 반지름을 살짝 흔들어 균일하지 않게 한다.
            var wobble = 0.86 + 0.14 * Math.Abs(Math.Sin(i * 2.3 + 0.6));
            var rx = width / 2.0 * baseRadiusFactor * wobble;
            var ry = height / 2.0 * baseRadiusFactor * wobble;
            points[i] = new Point(cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle));
        }

        // 두 점 사이 중간을 바깥/안쪽으로 민 제어점(중심을 지나치지 않게 최소 거리로 클램프).
        Point ControlBetween(Point a, Point b)
        {
            var mid = new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            var dx = mid.X - cx;
            var dy = mid.Y - cy;
            var len = Math.Max(0.0001, Math.Sqrt(dx * dx + dy * dy));
            var chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var dist = Math.Max(minDist, len + chord * pushFactor);
            return new Point(cx + dx / len * dist, cy + dy / len * dist);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (var i = 0; i < n; i++)
            {
                var next = points[(i + 1) % n];
                var control = ControlBetween(points[i], next);
                if (angular)
                {
                    context.LineTo(control, true, false);
                    context.LineTo(next, true, false);
                }
                else
                {
                    context.QuadraticBezierTo(control, next, true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static bool IsOnPanelBorder(ComicPanel panel, Point point)
    {
        const double borderHitSize = 18;
        var width = panel.Frame.ActualWidth;
        var height = panel.Frame.ActualHeight;

        // 프레임 밖(예: 칸을 넘어 튀어나온 크롭 OFF 이미지 영역)은 테두리가 아니다.
        // 이렇게 해야 넘친 이미지 위 클릭이 칸 테두리로 오인되지 않고 이미지 드래그로 처리된다.
        if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height)
        {
            return false;
        }

        return point.X <= borderHitSize ||
               point.Y <= borderHitSize ||
               point.X >= width - borderHitSize ||
               point.Y >= height - borderHitSize;
    }

    private static PanelImage? FindImageAtPoint(ComicPanel panel, Point panelPoint, bool includeLocked = false)
    {
        // 실제 화면 z-순서: 크롭 OFF 이미지(FreeImageCanvas)가 크롭 ON 이미지(ImageCanvas)보다 항상 앞에 있다.
        // 각 그룹 안에서는 panel.Images의 뒤쪽(높은 인덱스)이 위에 온다.
        // 따라서 크롭 OFF 그룹을 위에서부터 먼저 보고, 없으면 크롭 ON 그룹을 본다.
        return FindImageAtPointInGroup(panel, panelPoint, includeLocked, cropped: false)
            ?? FindImageAtPointInGroup(panel, panelPoint, includeLocked, cropped: true);
    }

    private static PanelImage? FindImageAtPointInGroup(ComicPanel panel, Point panelPoint, bool includeLocked, bool cropped)
    {
        for (var index = panel.Images.Count - 1; index >= 0; index--)
        {
            var image = panel.Images[index];
            if (image.IsCropped != cropped)
            {
                continue;
            }

            if (image.IsLocked && !includeLocked)
            {
                continue;
            }

            if (IsOpaqueImagePixelAtPoint(image, panelPoint))
            {
                return image;
            }
        }

        return null;
    }

    private static bool IsOpaqueImagePixelAtPoint(PanelImage image, Point panelPoint)
    {
        // 크롭 ON 이미지는 칸 사변형 밖에서는 화면에 잘려 보이지 않으므로 클릭 대상이 아니다.
        // (확대해 칸 밖으로 넘친 부분이 클릭을 가로채던 문제를 방지한다.)
        // 사변형 기하는 UpdatePanelShape가 항상 최신으로 유지하는 QuadFill.Data를 재사용한다(매번 새로 만들지 않음).
        if (image.IsCropped)
        {
            var clip = image.OwnerPanel.QuadFill.Data ?? CreatePanelQuadGeometry(image.OwnerPanel);
            if (!clip.FillContains(panelPoint))
            {
                return false;
            }
        }

        var content = image.Content;
        var transform = content.TransformToAncestor(image.OwnerPanel.Frame);
        var inverse = transform.Inverse;
        if (inverse == null)
        {
            return false;
        }

        var imagePoint = inverse.Transform(panelPoint);
        var controlWidth = content.ActualWidth > 0 ? content.ActualWidth : content.Width;
        var controlHeight = content.ActualHeight > 0 ? content.ActualHeight : content.Height;

        if (imagePoint.X < 0 || imagePoint.Y < 0 || imagePoint.X > controlWidth || imagePoint.Y > controlHeight)
        {
            return false;
        }

        // 동영상 등 비트맵이 없는 경우엔 사각형(컨트롤 영역) 기준으로 판정한다.
        var bitmap = GetAlphaBitmap(image);
        if (bitmap == null)
        {
            return true;
        }

        var scale = Math.Min(controlWidth / bitmap.PixelWidth, controlHeight / bitmap.PixelHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return false;
        }

        var drawnWidth = bitmap.PixelWidth * scale;
        var drawnHeight = bitmap.PixelHeight * scale;
        var offsetX = (controlWidth - drawnWidth) / 2;
        var offsetY = (controlHeight - drawnHeight) / 2;

        if (imagePoint.X < offsetX ||
            imagePoint.Y < offsetY ||
            imagePoint.X > offsetX + drawnWidth ||
            imagePoint.Y > offsetY + drawnHeight)
        {
            return false;
        }

        var pixelX = (int)Math.Clamp((imagePoint.X - offsetX) / scale, 0, bitmap.PixelWidth - 1);
        var pixelY = (int)Math.Clamp((imagePoint.Y - offsetY) / scale, 0, bitmap.PixelHeight - 1);
        return GetPixelAlpha(bitmap, pixelX, pixelY) > 8;
    }

    // 픽셀 알파를 읽기 위한 BGRA 변환본을 이미지별로 캐시한다(매 히트테스트마다 변환 비용 제거).
    // 소스가 바뀌면(애니/동영상 프레임 교체) Key 불일치로 자동 재생성된다.
    private static BitmapSource? GetAlphaBitmap(PanelImage image)
    {
        if (image.Image?.Source is not BitmapSource src)
        {
            return null;
        }

        if (ReferenceEquals(image.AlphaCacheKey, src) && image.AlphaCacheValue != null)
        {
            return image.AlphaCacheValue;
        }

        BitmapSource converted = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        if (converted.CanFreeze && !converted.IsFrozen)
        {
            converted.Freeze();
        }

        image.AlphaCacheKey = src;
        image.AlphaCacheValue = converted;
        return converted;
    }

    private static byte GetPixelAlpha(BitmapSource bgra, int x, int y)
    {
        // bgra는 GetAlphaBitmap이 BGRA로 보장한 비트맵이다.
        var pixels = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return pixels[3];
    }

    private static bool TryGetDroppedImagePaths(DragEventArgs e, out List<string> paths)
    {
        paths = new List<string>();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        paths = files.Where(IsSupportedImagePath).ToList();
        return paths.Count > 0;
    }

    private static bool IsSupportedImagePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp"
            or ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv" or ".m4v";
    }

    private static bool IsInsideResizeHandle(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsInsideBubble(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Border border && Equals(border.Tag, "SpeechBubble"))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static double GetCanvasLeft(FrameworkElement element)
    {
        var value = Canvas.GetLeft(element);
        return double.IsNaN(value) ? 0 : value;
    }

    private static double GetCanvasTop(FrameworkElement element)
    {
        var value = Canvas.GetTop(element);
        return double.IsNaN(value) ? 0 : value;
    }

    // 칸이 페이지 밖으로 넘어갈 수 있게 허용하되(넘어간 부분은 캔버스 클리핑으로 잘림),
    // 최소 MinPanelVisible 만큼은 페이지 안에 남겨 다시 잡을 수 있게 한다.
    private const double MinPanelVisible = 40;

    private double ClampPanelX(double x, double width)
    {
        return Math.Clamp(x, MinPanelVisible - width, _pageWidth - MinPanelVisible);
    }

    private double ClampPanelY(double y, double height)
    {
        return Math.Clamp(y, MinPanelVisible - height, _pageHeight - MinPanelVisible);
    }

    private static List<int> ParsePattern(string text)
    {
        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var count) ? count : 0)
            .Where(count => count > 0 && count <= 6)
            .ToList();
    }

    private static double ParseDoubleOr(string text, double fallback)
    {
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // 상대 경로 해석: 수동 불러오기면 저장 파일 폴더를 먼저, 그 다음 실행 파일 폴더 기준으로 찾는다.
    // (자동저장 복원 시에는 _projectBaseDirectory가 없어 실행 파일 폴더만 본다.) 절대 경로는 그대로 쓴다.
    private string ResolveProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
        {
            return path;
        }

        string? firstCandidate = null;
        foreach (var baseDirectory in EnumerateResolveBaseDirectories())
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDirectory, path));
            firstCandidate ??= candidate;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return firstCandidate!; // 어느 쪽에도 없으면 첫 후보(존재 확인은 호출부에서).
    }

    private IEnumerable<string> EnumerateResolveBaseDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_projectBaseDirectory))
        {
            yield return _projectBaseDirectory!;
        }

        yield return AppContext.BaseDirectory;
    }

    // 저장용 경로: 기준 폴더(또는 하위)면 그 기준 상대 경로, 그 외엔 절대 경로.
    // 기준 폴더 — 자동 저장(projectDirectory == null)은 실행 파일 폴더,
    //             수동 저장은 저장 파일 폴더로 판단한다.
    private static string MakeStorablePath(string path, string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return path; // 이미 상대거나 비어 있음.
        }

        var baseDirectory = string.IsNullOrWhiteSpace(projectDirectory)
            ? AppContext.BaseDirectory
            : projectDirectory!;

        return TryMakeRelativeUnder(baseDirectory, path) ?? path;
    }

    // fullPath가 baseDirectory 또는 그 하위면 상대 경로를, 아니면 null을 반환한다.
    private static string? TryMakeRelativeUnder(string baseDirectory, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(baseDirectory, fullPath);
            // baseDirectory 밖이면 ".."로 시작하거나 절대 경로가 된다 → 상대화하지 않는다.
            if (Path.IsPathFullyQualified(relative) ||
                relative.StartsWith("..", StringComparison.Ordinal))
            {
                return null;
            }

            return relative;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    private void LoadWindowSettings()
    {
        try
        {
            if (!File.Exists(_windowSettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(_windowSettingsPath);
            var settings = JsonSerializer.Deserialize<WindowSettings>(json);
            if (settings == null)
            {
                return;
            }

            Width = Math.Max(MinWidth, settings.Width);
            Height = Math.Max(MinHeight, settings.Height);

            if (settings.Left >= 0 && settings.Top >= 0)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.Left;
                Top = settings.Top;
            }

            // 앱 설정 복원
            LayoutPatternTextBox.Text = settings.LayoutPattern ?? "1,2,1";
            AutoMarginTextBox.Text = settings.AutoMargin ?? "24";
            AutoGutterTextBox.Text = settings.AutoGutter ?? "14";
            SetBubbleShapeByTag(settings.BubbleShape ?? "Oval");
            PageFitCheckBox.IsChecked = settings.PageFit;
            SetInspectorVisible(settings.InspectorVisible);
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void SetBubbleShapeByTag(string tag)
    {
        foreach (var item in BubbleShapeComboBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                BubbleShapeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void SaveWindowSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_windowSettingsPath)!);
            var settings = new WindowSettings
            {
                Width = RestoreBounds.Width,
                Height = RestoreBounds.Height,
                Left = RestoreBounds.Left,
                Top = RestoreBounds.Top,
                PageFit = PageFitCheckBox.IsChecked == true,
                LayoutPattern = LayoutPatternTextBox.Text,
                AutoMargin = AutoMarginTextBox.Text,
                AutoGutter = AutoGutterTextBox.Text,
                BubbleShape = (BubbleShapeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Oval",
                InspectorVisible = InspectorPanel.Visibility == Visibility.Visible
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_windowSettingsPath, json);
        }
        catch
        {
            // Window size persistence is a convenience; the editor should still close normally.
        }
    }
}

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
