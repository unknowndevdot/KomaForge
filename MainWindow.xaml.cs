using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;

namespace NovelViewer;

public partial class MainWindow : Window
{
    private const double PageWidth = 820;
    private const double PageHeight = 1120;

    private readonly List<ComicPanel> _panels = new();
    private ComicPanel? _selectedPanel;
    private SpeechBubble? _selectedBubble;
    private BubbleTail? _selectedBubbleTail;
    private PanelImage? _selectedImage;
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
    private System.Windows.Shapes.Path? _pageBubbleOutlinePath;
    private readonly string _windowSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NovelViewer",
        "window-settings.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowSettings();
        Closing += (_, _) => SaveWindowSettings();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        CreateLayoutFromPattern("1,2,1");
        _pages.Add(CaptureCurrentPage("Page 1"));
        UpdatePageList();
        UpdateInspectorLabels();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (DeleteSelectedBubble() || DeleteSelectedImage())
            {
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            var direction = e.Key == Key.Up ? -1 : 1;

            if (_selectedBubble != null)
            {
                MoveSelectedBubble(direction);
                e.Handled = true;
                return;
            }

            if (_selectedImage != null)
            {
                MoveSelectedImage(direction);
                e.Handled = true;
                return;
            }

            if (_selectedPanel != null)
            {
                MoveSelectedPanel(direction);
                e.Handled = true;
            }
        }
    }

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
        _pages.Add(new ComicPageData { Name = $"Page {_pages.Count + 1}" });
        _currentPageIndex = _pages.Count - 1;
        LoadPage(_pages[_currentPageIndex]);
        UpdatePageList();
        UpdateStatus("새 페이지를 추가했습니다.");
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

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPageState();

        var dialog = new SaveFileDialog
        {
            Title = "프로젝트 저장",
            Filter = "NovelViewer 프로젝트 (*.nvjson)|*.nvjson|JSON 파일 (*.json)|*.json",
            FileName = "NovelViewerProject.nvjson"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var project = new ComicProjectData
        {
            CurrentPageIndex = _currentPageIndex,
            Pages = CaptureProjectPages(Path.GetDirectoryName(dialog.FileName))
        };
        var json = JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dialog.FileName, json);
        _projectBaseDirectory = Path.GetDirectoryName(dialog.FileName);
        UpdateStatus("프로젝트를 저장했습니다.");
    }

    private void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "프로젝트 불러오기",
            Filter = "NovelViewer 프로젝트 (*.nvjson;*.json)|*.nvjson;*.json|모든 파일 (*.*)|*.*"
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

        foreach (var bubble in _selectedPanel.Bubbles.ToList())
        {
            RemoveBubbleFromCurrentParent(bubble);
        }

        PanelCanvas.Children.Remove(_selectedPanel.Frame);
        _panels.Remove(_selectedPanel);
        PanelListBox.Items.Remove(_selectedPanel);
        UpdatePanelOrder();
        if (_selectedImage?.OwnerPanel == _selectedPanel)
        {
            _selectedImage = null;
        }

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

    private void AddImageToPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("이미지를 넣을 칸을 먼저 선택하세요.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "칸에 넣을 이미지 선택",
            Filter = "이미지 파일 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|모든 파일 (*.*)|*.*",
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

        var bubble = CreateSpeechBubble(
            _selectedPanel,
            "대사를 입력하세요",
            BubbleWidthSlider.Value,
            BubbleHeightSlider.Value,
            BubbleFontSlider.Value,
            BubbleXSlider.Value,
            BubbleYSlider.Value);

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

        var tail = new BubbleTail
        {
            StartX = _selectedBubble.Container.Width / 2,
            StartY = _selectedBubble.Container.Height / 2,
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

    private void BubbleShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.Shape = GetSelectedBubbleShape();
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

        _panels.Clear();
        _selectedPanel = null;
        _selectedBubble = null;
        _nextPanelNumber = 1;
        PanelCanvas.Children.Clear();
        PageOverlay.Children.Clear();
        _pageBubbleOutlinePath = null;
        EnsurePageBubbleOutlinePath();
        PanelListBox.Items.Clear();
        UpdateImageList(null);
        UpdateBubbleList(null);

        var margin = 24.0;
        var gutter = 14.0;
        var rowHeight = Math.Min(280, (PageHeight - margin * 2 - gutter * (pattern.Count - 1)) / pattern.Count);
        var y = margin;

        foreach (var columns in pattern)
        {
            var panelWidth = (PageWidth - margin * 2 - gutter * (columns - 1)) / columns;
            var x = margin;

            for (var column = 0; column < columns; column++)
            {
                AddPanel(CreatePanel(_nextPanelNumber++, x, y, panelWidth, rowHeight));
                x += panelWidth + gutter;
            }

            y += rowHeight + gutter;
        }

        if (_panels.Count > 0)
        {
            SelectPanel(_panels[0]);
        }

        UpdateLayoutSummary();
        UpdateStatus("기본 칸 구성을 생성했습니다.");
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
        var page = new ComicPageData { Name = name };

        foreach (var panel in _panels)
        {
            var panelData = new ComicPanelData
            {
                Number = panel.Number,
                X = GetCanvasLeft(panel.Frame),
                Y = GetCanvasTop(panel.Frame),
                Width = panel.Frame.Width,
                Height = panel.Frame.Height
            };

            foreach (var image in panel.Images)
            {
                panelData.Images.Add(new PanelImageData
                {
                    Path = image.Path,
                    Scale = image.Scale.ScaleX,
                    TranslateX = image.Translate.X,
                    TranslateY = image.Translate.Y
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
                    IsCropped = bubble.IsCropped,
                    Shape = bubble.Shape.ToString(),
                    Tails = bubble.Tails
                        .Select(tail => new BubbleTailData
                        {
                            StartX = tail.StartX,
                            StartY = tail.StartY,
                            X = tail.X,
                            Y = tail.Y,
                            Width = tail.Width
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
            var copiedPage = new ComicPageData { Name = page.Name };

            foreach (var panel in page.Panels)
            {
                var copiedPanel = new ComicPanelData
                {
                    Number = panel.Number,
                    X = panel.X,
                    Y = panel.Y,
                    Width = panel.Width,
                    Height = panel.Height,
                    Bubbles = panel.Bubbles
                };

                foreach (var image in panel.Images)
                {
                    copiedPanel.Images.Add(new PanelImageData
                    {
                        Path = MakeRelativePath(projectDirectory, image.Path),
                        Scale = image.Scale,
                        TranslateX = image.TranslateX,
                        TranslateY = image.TranslateY
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

                bubble.Shape = Enum.TryParse<BubbleShape>(bubbleData.Shape, out var shape)
                    ? shape
                    : BubbleShape.Oval;
                bubble.Tails.Clear();
                bubble.Tails.AddRange(bubbleData.Tails.Select(tail => new BubbleTail
                {
                    StartX = tail.StartX,
                    StartY = tail.StartY,
                    X = tail.X,
                    Y = tail.Y,
                    Width = tail.Width
                }));
                UpdateBubbleGeometry(bubble);

                AttachBubbleToPanelOverlay(bubble);
                if (!bubbleData.IsCropped)
                {
                    SetBubbleCrop(bubble, false);
                }

                panel.Bubbles.Add(bubble);
            }

            UpdateBubbleOrder(panel);
            UpdateMergedBubbleOutlines();
            panel.SelectedImage = panel.Images.LastOrDefault();
            panel.Placeholder.Visibility = panel.Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_panels.Count > 0)
        {
            SelectPanel(_panels[0]);
        }
        else
        {
            ClearSelection();
        }

        UpdateLayoutSummary();
    }

    private void ClearPageVisuals()
    {
        _panels.Clear();
        _selectedPanel = null;
        _selectedBubble = null;
        _selectedImage = null;
        PanelCanvas.Children.Clear();
        PageOverlay.Children.Clear();
        _pageBubbleOutlinePath = null;
        EnsurePageBubbleOutlinePath();
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
        var imageCanvas = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent
        };

        var placeholder = new TextBlock
        {
            Text = $"{number}번 칸",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(116, 111, 102)),
            FontSize = 18
        };

        var overlay = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent
        };
        var bubbleOutlinePath = CreateBubbleOutlinePath();
        overlay.Children.Add(bubbleOutlinePath);
        Panel.SetZIndex(bubbleOutlinePath, int.MaxValue - 1);

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

        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(imageCanvas);
        grid.Children.Add(placeholder);
        grid.Children.Add(overlay);
        grid.Children.Add(resizeHandle);

        var frame = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(3),
            Child = grid,
            ClipToBounds = true,
            Cursor = Cursors.SizeAll,
            Tag = number
        };
        frame.AllowDrop = true;

        var panel = new ComicPanel(number, frame, imageCanvas, placeholder, overlay, bubbleOutlinePath, resizeHandle);
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

        return panel;
    }

    private SpeechBubble CreateSpeechBubble(ComicPanel ownerPanel, string text, double width, double height, double fontSize, double x, double y)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = new FontFamily("Malgun Gothic"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(16, 12, 16, 12),
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

        var bodyPath = new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
            IsHitTestVisible = false
        };

        var tailStartHandle = CreateTailHandle();
        var tailEndHandle = CreateTailHandle();

        var content = new Grid();
        content.Children.Add(bodyPath);
        content.Children.Add(textBlock);
        content.Children.Add(tailStartHandle);
        content.Children.Add(tailEndHandle);
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

        var bubble = new SpeechBubble(ownerPanel, container, bodyPath, textBlock, resizeHandle, tailStartHandle, tailEndHandle)
        {
            Shape = GetSelectedBubbleShape()
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
        tailStartHandle.DragDelta += (_, e) => DragBubbleTailPoint(bubble, true, e);
        tailEndHandle.DragDelta += (_, e) => DragBubbleTailPoint(bubble, false, e);

        return bubble;
    }

    private void BeginPanelDrag(ComicPanel panel, MouseButtonEventArgs e)
    {
        SelectPanel(panel);
        if (IsInsideResizeHandle(e.OriginalSource as DependencyObject) || IsInsideBubble(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (IsOnPanelBorder(panel, e.GetPosition(panel.Frame)))
        {
            _isDraggingPanel = true;
            _dragStart = e.GetPosition(panel.Frame);
        }
        else
        {
            var image = FindImageAtPoint(panel, e.GetPosition(panel.Frame));
            if (image == null)
            {
                return;
            }

            SelectImage(image);
            _isDraggingPanelImage = true;
            _imageDragStart = e.GetPosition(panel.Frame);
            panel.Frame.Cursor = Cursors.Hand;
        }

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

        SelectPanel(panel);
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

        try
        {
            PanelImage? lastImage = null;
            foreach (var path in paths)
            {
                lastImage = AddPanelImage(panel, path);
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

        var x = GetCanvasLeft(panel.Frame);
        var y = GetCanvasTop(panel.Frame);
        var width = Math.Clamp(panel.Frame.Width + e.HorizontalChange, PanelWidthSlider.Minimum, PageWidth - x);
        var height = Math.Clamp(panel.Frame.Height + e.VerticalChange, PanelHeightSlider.Minimum, PageHeight - y);

        panel.Frame.Width = width;
        panel.Frame.Height = height;
        UpdatePanelImageSizes(panel);
        LoadPanelValues(panel);
        UpdateFreeBubblesForPanel(panel);
    }

    private void BeginBubbleDrag(SpeechBubble bubble, MouseButtonEventArgs e)
    {
        SelectBubble(bubble);
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

        var relative = GetBubblePositionInOwnerPanel(bubble);
        BubbleXSlider.Value = Math.Clamp(relative.X, BubbleXSlider.Minimum, BubbleXSlider.Maximum);
        BubbleYSlider.Value = Math.Clamp(relative.Y, BubbleYSlider.Minimum, BubbleYSlider.Maximum);
        UpdateMergedBubbleOutlines();
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
    }

    private void SelectPanel(ComicPanel panel)
    {
        _selectedPanel = panel;
        PanelListBox.SelectedItem = panel;
        UpdateImageList(panel);
        UpdateBubbleList(panel);

        if (_selectedBubble == null || _selectedBubble.OwnerPanel != panel)
        {
            _selectedBubble = panel.Bubbles.LastOrDefault();
        }

        if (_selectedImage == null || _selectedImage.OwnerPanel != panel)
        {
            _selectedImage = panel.Images.LastOrDefault();
            panel.SelectedImage = _selectedImage;
        }

        LoadPanelValues(panel);

        if (_selectedBubble != null)
        {
            LoadBubbleValues(_selectedBubble);
            BubbleListBox.SelectedItem = _selectedBubble;
        }

        UpdateSelectionLabels();
        UpdateSelectionVisuals();
    }

    private void SelectBubble(SpeechBubble bubble)
    {
        _selectedBubble = bubble;
        _selectedBubbleTail = bubble.Tails.FirstOrDefault();
        _selectedPanel = bubble.OwnerPanel;
        PanelListBox.SelectedItem = bubble.OwnerPanel;

        if (_selectedImage == null || _selectedImage.OwnerPanel != bubble.OwnerPanel)
        {
            _selectedImage = bubble.OwnerPanel.Images.LastOrDefault();
            bubble.OwnerPanel.SelectedImage = _selectedImage;
        }

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
        _isLoadingInspector = false;
        UpdateBubbleTailHandles(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        BubbleTailListBox.SelectedItem = tail;
        UpdateInspectorLabels();
    }

    private void SelectImage(PanelImage image)
    {
        _selectedImage = image;
        _selectedPanel = image.OwnerPanel;
        image.OwnerPanel.SelectedImage = image;
        PanelListBox.SelectedItem = image.OwnerPanel;
        UpdateImageList(image.OwnerPanel);
        ImageListBox.SelectedItem = image;
        LoadPanelValues(image.OwnerPanel);
        UpdateSelectionVisuals();
    }

    private void ApplyPanelValues(ComicPanel panel)
    {
        var width = Math.Min(PanelWidthSlider.Value, PageWidth - PanelXSlider.Value);
        var height = Math.Min(PanelHeightSlider.Value, PageHeight - PanelYSlider.Value);
        panel.Frame.Width = width;
        panel.Frame.Height = height;
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
        BubbleShapeComboBox.SelectedIndex = bubble.Shape switch
        {
            BubbleShape.Rectangle => 1,
            BubbleShape.Cloud => 2,
            BubbleShape.Shout => 3,
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
            AttachBubbleToPageOverlay(bubble);
        }

        SetBubblePositionInOwnerPanel(bubble, position.X, position.Y);
        UpdateBubbleOrder(bubble.OwnerPanel);
        UpdateStatus(isCropped ? "말풍선을 칸 안에서 자릅니다." : "말풍선이 종이 안에서 다른 칸 위로도 보입니다.");
    }

    private void AttachBubbleToPanelOverlay(SpeechBubble bubble)
    {
        bubble.OwnerPanel.Overlay.Children.Add(bubble.Container);
        bubble.IsCropped = true;
    }

    private void AttachBubbleToPageOverlay(SpeechBubble bubble)
    {
        PageOverlay.Children.Add(bubble.Container);
        bubble.IsCropped = false;
    }

    private void RemoveBubbleFromCurrentParent(SpeechBubble bubble)
    {
        if (bubble.Container.Parent is Canvas canvas)
        {
            canvas.Children.Remove(bubble.Container);
        }

        UpdateMergedBubbleOutlines();
    }

    private void UpdateFreeBubblesForPanel(ComicPanel panel)
    {
        foreach (var bubble in panel.Bubbles.Where(item => !item.IsCropped))
        {
            SetBubblePositionInOwnerPanel(bubble, bubble.RelativeX, bubble.RelativeY);
        }
    }

    private Point GetBubblePositionInOwnerPanel(SpeechBubble bubble)
    {
        var left = GetCanvasLeft(bubble.Container);
        var top = GetCanvasTop(bubble.Container);

        if (bubble.IsCropped)
        {
            bubble.RelativeX = left;
            bubble.RelativeY = top;
            return new Point(left, top);
        }

        var panelOrigin = bubble.OwnerPanel.Overlay.TransformToVisual(PageOverlay).Transform(new Point(0, 0));
        bubble.RelativeX = left - panelOrigin.X;
        bubble.RelativeY = top - panelOrigin.Y;
        return new Point(bubble.RelativeX, bubble.RelativeY);
    }

    private void SetBubblePositionInOwnerPanel(SpeechBubble bubble, double x, double y)
    {
        bubble.RelativeX = x;
        bubble.RelativeY = y;

        if (bubble.IsCropped)
        {
            Canvas.SetLeft(bubble.Container, x);
            Canvas.SetTop(bubble.Container, y);
            UpdateMergedBubbleOutlines();
            return;
        }

        var panelOrigin = bubble.OwnerPanel.Overlay.TransformToVisual(PageOverlay).Transform(new Point(0, 0));
        Canvas.SetLeft(bubble.Container, panelOrigin.X + x);
        Canvas.SetTop(bubble.Container, panelOrigin.Y + y);
        UpdateMergedBubbleOutlines();
    }

    private void SetPanelPosition(ComicPanel panel, double x, double y)
    {
        Canvas.SetLeft(panel.Frame, ClampPanelX(x, panel.Frame.Width));
        Canvas.SetTop(panel.Frame, ClampPanelY(y, panel.Frame.Height));
    }

    private void UpdateSelectionLabels()
    {
        SelectedBubbleText.Text = _selectedBubble == null
            ? "선택한 말풍선 없음"
            : "선택한 말풍선 조절 중";

        if (_selectedBubble == null && SelectedBubbleTextBox != null)
        {
            _isLoadingInspector = true;
            SelectedBubbleTextBox.Text = string.Empty;
            BubbleCropCheckBox.IsChecked = false;
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
        _selectedPanel = null;
        _selectedBubble = null;
        _selectedBubbleTail = null;
        _selectedImage = null;
        PanelListBox.SelectedItem = null;
        UpdateImageList(null);
        UpdateBubbleList(null);
        UpdateBubbleTailList(null);
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
        UpdateStatus("선택을 해제했습니다.");
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var panel in _panels)
        {
            var isSelectedPanel = panel == _selectedPanel;
            panel.Frame.BorderBrush = isSelectedPanel
                ? new SolidColorBrush(Color.FromRgb(43, 111, 106))
                : Brushes.Black;
            panel.ResizeHandle.Visibility = isSelectedPanel ? Visibility.Visible : Visibility.Hidden;

            foreach (var bubble in panel.Bubbles)
            {
                var isSelectedBubble = bubble == _selectedBubble;
                bubble.BodyPath.Stroke = Brushes.Transparent;
                bubble.BodyPath.StrokeThickness = 0;
                bubble.ResizeHandle.Visibility = isSelectedBubble ? Visibility.Visible : Visibility.Hidden;
                UpdateBubbleTailHandles(bubble);
            }

            foreach (var image in panel.Images)
            {
                image.SelectionBorder.Visibility = image == _selectedImage
                    ? Visibility.Visible
                    : Visibility.Hidden;
            }
        }
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
            BubbleYText == null)
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
        return AddPanelImage(panel, LoadBitmap(path), path);
    }

    private PanelImage AddPanelImage(ComicPanel panel, BitmapSource bitmap, string path)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translate);

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var selectionBorder = new Border
        {
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            BorderBrush = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden
        };

        var layer = new Grid
        {
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            ClipToBounds = true
        };
        layer.Children.Add(image);
        layer.Children.Add(selectionBorder);

        var panelImage = new PanelImage(panel, path, layer, image, selectionBorder, scale, translate);
        panel.Images.Add(panelImage);
        panel.ImageCanvas.Children.Add(layer);
        panel.SelectedImage = panelImage;
        _selectedImage = panelImage;
        UpdatePanelImageSizes(panel);
        UpdateImageOrder(panel);
        UpdateImageList(panel);
        panel.Placeholder.Visibility = Visibility.Collapsed;
        return panelImage;
    }

    private void RemovePanelImage(PanelImage image)
    {
        var panel = image.OwnerPanel;
        panel.ImageCanvas.Children.Remove(image.Layer);
        panel.Images.Remove(image);

        if (_selectedImage == image)
        {
            _selectedImage = panel.Images.LastOrDefault();
        }

        panel.SelectedImage = _selectedImage?.OwnerPanel == panel ? _selectedImage : panel.Images.LastOrDefault();
        _selectedImage = panel.SelectedImage;
        panel.Placeholder.Visibility = panel.Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        var selectedPanel = _selectedPanel;
        PanelListBox.Items.Clear();

        foreach (var panel in _panels)
        {
            PanelListBox.Items.Add(panel);
        }

        PanelListBox.SelectedItem = selectedPanel;
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
        for (var index = 0; index < panel.Bubbles.Count; index++)
        {
            Panel.SetZIndex(panel.Bubbles[index].Container, index);
        }
    }

    private static void UpdatePanelImageSizes(ComicPanel panel)
    {
        foreach (var image in panel.Images)
        {
            image.Layer.Width = panel.Frame.Width;
            image.Layer.Height = panel.Frame.Height;
            image.Image.Width = panel.Frame.Width;
            image.Image.Height = panel.Frame.Height;
            image.SelectionBorder.Width = panel.Frame.Width;
            image.SelectionBorder.Height = panel.Frame.Height;
        }
    }

    private void UpdateBubbleGeometry(SpeechBubble bubble)
    {
        var width = Math.Max(1, bubble.Container.Width);
        var height = Math.Max(1, bubble.Container.Height);

        bubble.BodyPath.Data = bubble.Shape switch
        {
            BubbleShape.Rectangle => CreateRectangleGeometry(width, height),
            BubbleShape.Cloud => CreateCloudGeometry(width, height),
            BubbleShape.Shout => CreateShoutGeometry(width, height),
            _ => CreateOvalGeometry(width, height)
        };

        UpdateBubbleTailHandles(bubble);
        UpdateMergedBubbleOutlines();
    }

    private void UpdateMergedBubbleOutlines()
    {
        foreach (var panel in _panels)
        {
            panel.BubbleOutlinePath.Data = CreateMergedBubbleGeometry(panel.Bubbles.Where(bubble => bubble.IsCropped));
        }

        EnsurePageBubbleOutlinePath();
        if (_pageBubbleOutlinePath != null)
        {
            _pageBubbleOutlinePath.Data = CreateMergedBubbleGeometry(_panels.SelectMany(panel => panel.Bubbles).Where(bubble => !bubble.IsCropped));
        }
    }

    private static Geometry CreateMergedBubbleGeometry(IEnumerable<SpeechBubble> bubbles)
    {
        Geometry? merged = null;

        foreach (var bubble in bubbles)
        {
            if (bubble.BodyPath.Data == null)
            {
                continue;
            }

            var geometry = bubble.BodyPath.Data.Clone();
            geometry.Transform = new TranslateTransform(GetCanvasLeft(bubble.Container), GetCanvasTop(bubble.Container));

            merged = merged == null
                ? geometry
                : Geometry.Combine(merged, geometry, GeometryCombineMode.Union, null);

            foreach (var tail in bubble.Tails)
            {
                var tailGeometry = CreateTailGeometry(tail);
                tailGeometry.Transform = new TranslateTransform(GetCanvasLeft(bubble.Container), GetCanvasTop(bubble.Container));
                merged = Geometry.Combine(merged, tailGeometry, GeometryCombineMode.Union, null);
            }
        }

        return merged ?? Geometry.Empty;
    }

    private static Geometry CreateTailGeometry(BubbleTail tail)
    {
        var start = new Point(tail.StartX, tail.StartY);
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
        var control = new Point((start.X + end.X) / 2 + normal.X * halfWidth, (start.Y + end.Y) / 2 + normal.Y * halfWidth);

        var figure = new PathFigure { StartPoint = startA, IsClosed = true };
        figure.Segments.Add(new QuadraticBezierSegment(control, end, true));
        figure.Segments.Add(new QuadraticBezierSegment(control, startB, true));
        return new PathGeometry(new[] { figure });
    }

    private static Thumb CreateTailHandle()
    {
        return new Thumb
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.SizeAll,
            Background = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Hidden
        };
    }

    private void DragBubbleTailPoint(SpeechBubble bubble, bool isStartPoint, DragDeltaEventArgs e)
    {
        SelectBubble(bubble);

        if (_selectedBubbleTail == null)
        {
            return;
        }

        if (isStartPoint)
        {
            _selectedBubbleTail.StartX += e.HorizontalChange;
            _selectedBubbleTail.StartY += e.VerticalChange;
        }
        else
        {
            _selectedBubbleTail.X += e.HorizontalChange;
            _selectedBubbleTail.Y += e.VerticalChange;
        }

        _isLoadingInspector = true;
        BubbleTailXSlider.Value = Math.Clamp(_selectedBubbleTail.X, BubbleTailXSlider.Minimum, BubbleTailXSlider.Maximum);
        BubbleTailYSlider.Value = Math.Clamp(_selectedBubbleTail.Y, BubbleTailYSlider.Minimum, BubbleTailYSlider.Maximum);
        BubbleTailWidthSlider.Value = Math.Clamp(_selectedBubbleTail.Width, BubbleTailWidthSlider.Minimum, BubbleTailWidthSlider.Maximum);
        _isLoadingInspector = false;
        UpdateBubbleGeometry(bubble);
        UpdateBubbleTailList(bubble);
    }

    private void UpdateBubbleTailHandles(SpeechBubble bubble)
    {
        var showHandles = bubble == _selectedBubble && _selectedBubbleTail != null && bubble.Tails.Contains(_selectedBubbleTail);
        bubble.TailStartHandle.Visibility = showHandles ? Visibility.Visible : Visibility.Hidden;
        bubble.TailEndHandle.Visibility = showHandles ? Visibility.Visible : Visibility.Hidden;

        if (!showHandles || _selectedBubbleTail == null)
        {
            return;
        }

        bubble.TailStartHandle.Margin = new Thickness(_selectedBubbleTail.StartX - 7, _selectedBubbleTail.StartY - 7, 0, 0);
        bubble.TailEndHandle.Margin = new Thickness(_selectedBubbleTail.X - 7, _selectedBubbleTail.Y - 7, 0, 0);
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

    private void EnsurePageBubbleOutlinePath()
    {
        if (_pageBubbleOutlinePath != null && PageOverlay.Children.Contains(_pageBubbleOutlinePath))
        {
            return;
        }

        _pageBubbleOutlinePath = CreateBubbleOutlinePath();
        PageOverlay.Children.Add(_pageBubbleOutlinePath);
        Panel.SetZIndex(_pageBubbleOutlinePath, int.MaxValue - 1);
    }

    private BubbleShape GetSelectedBubbleShape()
    {
        if (BubbleShapeComboBox?.SelectedItem is not ComboBoxItem item)
        {
            return BubbleShape.Oval;
        }

        return item.Tag?.ToString() switch
        {
            "Rectangle" => BubbleShape.Rectangle,
            "Cloud" => BubbleShape.Cloud,
            "Shout" => BubbleShape.Shout,
            _ => BubbleShape.Oval
        };
    }

    private static Geometry CreateOvalGeometry(double width, double height)
    {
        return new EllipseGeometry(new Rect(1, 1, Math.Max(1, width - 2), Math.Max(1, height - 2)));
    }

    private static Geometry CreateRectangleGeometry(double width, double height)
    {
        return new RectangleGeometry(new Rect(1, 1, Math.Max(1, width - 2), Math.Max(1, height - 2)), 8, 8);
    }

    private static Geometry CreateCloudGeometry(double width, double height)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();

        context.BeginFigure(new Point(width * 0.14, height * 0.55), true, true);
        context.BezierTo(new Point(width * 0.02, height * 0.47), new Point(width * 0.06, height * 0.28), new Point(width * 0.23, height * 0.30), true, false);
        context.BezierTo(new Point(width * 0.24, height * 0.08), new Point(width * 0.50, height * 0.06), new Point(width * 0.56, height * 0.24), true, false);
        context.BezierTo(new Point(width * 0.75, height * 0.12), new Point(width * 0.95, height * 0.27), new Point(width * 0.86, height * 0.47), true, false);
        context.BezierTo(new Point(width * 1.00, height * 0.61), new Point(width * 0.87, height * 0.84), new Point(width * 0.68, height * 0.75), true, false);
        context.BezierTo(new Point(width * 0.56, height * 0.95), new Point(width * 0.25, height * 0.88), new Point(width * 0.30, height * 0.70), true, false);
        context.BezierTo(new Point(width * 0.18, height * 0.75), new Point(width * 0.06, height * 0.68), new Point(width * 0.14, height * 0.55), true, false);

        geometry.Freeze();
        return geometry;
    }

    private static Geometry CreateShoutGeometry(double width, double height)
    {
        var points = new[]
        {
            new Point(width * 0.08, height * 0.12),
            new Point(width * 0.23, height * 0.04),
            new Point(width * 0.34, height * 0.13),
            new Point(width * 0.51, height * 0.03),
            new Point(width * 0.64, height * 0.14),
            new Point(width * 0.85, height * 0.09),
            new Point(width * 0.77, height * 0.31),
            new Point(width * 0.95, height * 0.45),
            new Point(width * 0.79, height * 0.58),
            new Point(width * 0.87, height * 0.86),
            new Point(width * 0.62, height * 0.77),
            new Point(width * 0.48, height * 0.96),
            new Point(width * 0.36, height * 0.78),
            new Point(width * 0.10, height * 0.87),
            new Point(width * 0.18, height * 0.61),
            new Point(width * 0.02, height * 0.46),
            new Point(width * 0.19, height * 0.32)
        };

        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(points[0], true, true);
        context.PolyLineTo(points.Skip(1).ToList(), true, false);
        geometry.Freeze();
        return geometry;
    }

    private static bool IsOnPanelBorder(ComicPanel panel, Point point)
    {
        const double borderHitSize = 12;
        return point.X <= borderHitSize ||
               point.Y <= borderHitSize ||
               point.X >= panel.Frame.ActualWidth - borderHitSize ||
               point.Y >= panel.Frame.ActualHeight - borderHitSize;
    }

    private static PanelImage? FindImageAtPoint(ComicPanel panel, Point panelPoint)
    {
        for (var index = panel.Images.Count - 1; index >= 0; index--)
        {
            var image = panel.Images[index];
            if (IsOpaqueImagePixelAtPoint(image, panelPoint))
            {
                return image;
            }
        }

        return null;
    }

    private static bool IsOpaqueImagePixelAtPoint(PanelImage image, Point panelPoint)
    {
        if (image.Image.Source is not BitmapSource bitmap)
        {
            return false;
        }

        var transform = image.Image.TransformToAncestor(image.OwnerPanel.Frame);
        var inverse = transform.Inverse;
        if (inverse == null)
        {
            return false;
        }

        var imagePoint = inverse.Transform(panelPoint);
        var controlWidth = image.Image.ActualWidth > 0 ? image.Image.ActualWidth : image.Image.Width;
        var controlHeight = image.Image.ActualHeight > 0 ? image.Image.ActualHeight : image.Image.Height;

        if (imagePoint.X < 0 || imagePoint.Y < 0 || imagePoint.X > controlWidth || imagePoint.Y > controlHeight)
        {
            return false;
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

    private static byte GetPixelAlpha(BitmapSource bitmap, int x, int y)
    {
        var converted = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        var pixels = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
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
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
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

    private static double ClampPanelX(double x, double width)
    {
        return Math.Clamp(x, 0, Math.Max(0, PageWidth - width));
    }

    private static double ClampPanelY(double y, double height)
    {
        return Math.Clamp(y, 0, Math.Max(0, PageHeight - height));
    }

    private static List<int> ParsePattern(string text)
    {
        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var count) ? count : 0)
            .Where(count => count > 0 && count <= 6)
            .ToList();
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

    private string ResolveProjectPath(string path)
    {
        if (Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(_projectBaseDirectory))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(_projectBaseDirectory, path));
    }

    private static string MakeRelativePath(string? baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathFullyQualified(path))
        {
            return path;
        }

        try
        {
            return Path.GetRelativePath(baseDirectory, path);
        }
        catch
        {
            return path;
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
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
                Top = RestoreBounds.Top
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
        System.Windows.Shapes.Path bubbleOutlinePath,
        Thumb resizeHandle)
    {
        Number = number;
        Frame = frame;
        ImageCanvas = imageCanvas;
        Placeholder = placeholder;
        Overlay = overlay;
        BubbleOutlinePath = bubbleOutlinePath;
        ResizeHandle = resizeHandle;
    }

    public int Number { get; }
    public Border Frame { get; }
    public Canvas ImageCanvas { get; }
    public TextBlock Placeholder { get; }
    public Canvas Overlay { get; }
    public System.Windows.Shapes.Path BubbleOutlinePath { get; }
    public Thumb ResizeHandle { get; }
    public List<PanelImage> Images { get; } = new();
    public PanelImage? SelectedImage { get; set; }
    public List<SpeechBubble> Bubbles { get; } = new();

    public override string ToString()
    {
        return $"{Number}번 칸";
    }
}

public sealed class PanelImage
{
    public PanelImage(
        ComicPanel ownerPanel,
        string path,
        Grid layer,
        Image image,
        Border selectionBorder,
        ScaleTransform scale,
        TranslateTransform translate)
    {
        OwnerPanel = ownerPanel;
        Path = path;
        Layer = layer;
        Image = image;
        SelectionBorder = selectionBorder;
        Scale = scale;
        Translate = translate;
    }

    public ComicPanel OwnerPanel { get; }
    public string Path { get; }
    public Grid Layer { get; }
    public Image Image { get; }
    public Border SelectionBorder { get; }
    public ScaleTransform Scale { get; }
    public TranslateTransform Translate { get; }

    public override string ToString()
    {
        var index = OwnerPanel.Images.IndexOf(this) + 1;
        return $"{index}번 이미지 - {System.IO.Path.GetFileName(Path)}";
    }
}

public sealed class SpeechBubble
{
    public SpeechBubble(
        ComicPanel ownerPanel,
        Border container,
        System.Windows.Shapes.Path bodyPath,
        TextBlock textBlock,
        Thumb resizeHandle,
        Thumb tailStartHandle,
        Thumb tailEndHandle)
    {
        OwnerPanel = ownerPanel;
        Container = container;
        BodyPath = bodyPath;
        TextBlock = textBlock;
        ResizeHandle = resizeHandle;
        TailStartHandle = tailStartHandle;
        TailEndHandle = tailEndHandle;
    }

    public ComicPanel OwnerPanel { get; }
    public Border Container { get; }
    public System.Windows.Shapes.Path BodyPath { get; }
    public TextBlock TextBlock { get; }
    public Thumb ResizeHandle { get; }
    public Thumb TailStartHandle { get; }
    public Thumb TailEndHandle { get; }
    public bool IsCropped { get; set; } = true;
    public BubbleShape Shape { get; set; } = BubbleShape.Oval;
    public List<BubbleTail> Tails { get; } = new();
    public double RelativeX { get; set; }
    public double RelativeY { get; set; }

    public override string ToString()
    {
        var index = OwnerPanel.Bubbles.IndexOf(this) + 1;
        var preview = TextBlock.Text.ReplaceLineEndings(" ").Trim();

        if (preview.Length > 18)
        {
            preview = preview[..18] + "...";
        }

        return string.IsNullOrWhiteSpace(preview)
            ? $"{index}번 말풍선"
            : $"{index}번 말풍선 - {preview}";
    }
}

public sealed class BubbleTail
{
    public double StartX { get; set; } = 85;
    public double StartY { get; set; } = 50;
    public double X { get; set; } = 130;
    public double Y { get; set; } = 130;
    public double Width { get; set; } = 28;

    public override string ToString()
    {
        return $"꼬리 ({X:0}, {Y:0})";
    }
}

public enum BubbleShape
{
    Oval,
    Rectangle,
    Cloud,
    Shout
}

public sealed class ComicProjectData
{
    public int CurrentPageIndex { get; set; }
    public List<ComicPageData> Pages { get; set; } = new();
}

public sealed class ComicPageData
{
    public string Name { get; set; } = "Page";
    public List<ComicPanelData> Panels { get; set; } = new();
}

public sealed class ComicPanelData
{
    public int Number { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<PanelImageData> Images { get; set; } = new();
    public List<SpeechBubbleData> Bubbles { get; set; } = new();
}

public sealed class PanelImageData
{
    public string Path { get; set; } = string.Empty;
    public double Scale { get; set; } = 1;
    public double TranslateX { get; set; }
    public double TranslateY { get; set; }
}

public sealed class SpeechBubbleData
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 170;
    public double Height { get; set; } = 100;
    public double FontSize { get; set; } = 18;
    public bool IsCropped { get; set; }
    public string Shape { get; set; } = nameof(BubbleShape.Oval);
    public List<BubbleTailData> Tails { get; set; } = new();
}

public sealed class BubbleTailData
{
    public double StartX { get; set; } = 85;
    public double StartY { get; set; } = 50;
    public double X { get; set; } = 130;
    public double Y { get; set; } = 130;
    public double Width { get; set; } = 28;
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
}
