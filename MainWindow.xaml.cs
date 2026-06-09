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

            // Ctrl+X/C/V: 칸·이미지·말풍선 잘라내기/복사/붙여넣기.
            // 텍스트 입력 중에는 기본 텍스트 편집(잘라내기/복사/붙여넣기)에 양보한다.
            if ((e.Key == Key.X || e.Key == Key.C || e.Key == Key.V) && Keyboard.FocusedElement is not TextBox)
            {
                if (e.Key == Key.X)
                {
                    CutSelection();
                }
                else if (e.Key == Key.C)
                {
                    CopySelection();
                }
                else
                {
                    PasteClipboard();
                }

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

    // "페이지 쪽 맞춤"이 켜져 있으면 페이지 전체가 보이도록 뷰 영역에 맞춰 축소/확대한다.
    private void UpdatePageFit()
    {
        if (PageFrame == null || PageScrollViewer == null)
        {
            return;
        }

        if (PageFitMenuItem?.IsChecked != true)
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

}
