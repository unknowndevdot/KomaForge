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
        // 편집 대상: VN 템플릿을 편집 중이면 그 템플릿, 아니면 현재 일반 페이지.
        var target = _editingTemplate;
        if (target == null)
        {
            if (_pages.Count == 0 || _currentPageIndex < 0 || _currentPageIndex >= _pages.Count)
            {
                return;
            }
            target = _pages[_currentPageIndex];
        }

        // 같은 객체를 유지한 채 내용만 갱신한다. 객체를 교체하면 페이지 리스트(바인딩)가
        // 옛 인스턴스를 보게 되어 인라인 이름 편집(IsEditing) 등이 동작하지 않는다.
        var captured = CaptureCurrentPage(target.Name);
        target.PageWidth = captured.PageWidth;
        target.PageHeight = captured.PageHeight;
        target.BackgroundColor = captured.BackgroundColor;
        target.Panels = captured.Panels;
    }

    private ComicPageData CaptureCurrentPage(string name)
    {
        var page = new ComicPageData
        {
            Name = name,
            PageWidth = _pageWidth,
            PageHeight = _pageHeight,
            BackgroundColor = ColorToHex(CurrentPageBackgroundColor())
        };

        foreach (var panel in _panels)
        {
            page.Panels.Add(CapturePanelData(panel));
        }

        return page;
    }

    // --- 단일 오브젝트를 직렬화 DTO로 캡처(저장·클립보드 공용) ---

    private static PanelImageData CaptureImageData(PanelImage image) => new()
    {
        Id = image.Id,
        Path = image.Path,
        Scale = image.Scale.ScaleX,
        ScaleY = image.Scale.ScaleY,
        TranslateX = image.Translate.X,
        TranslateY = image.Translate.Y,
        // Scale·Translate가 기준으로 삼는 콘텐츠 박스 크기(= 추가 당시 칸 크기). 함께 저장해야 재로딩 시 위치/크기가 보존된다.
        BaseWidth = image.Content.Width,
        BaseHeight = image.Content.Height,
        IsCropped = image.IsCropped,
        IsLocked = image.IsLocked,
        PivotX = image.PivotX,
        PivotY = image.PivotY,
        GradientDirection = image.GradientDirection.ToString(),
        GradientColor = ColorToHex(image.GradientColor),
        GradientStart = image.GradientStart,
        GradientEnd = image.GradientEnd,
        OutputDuration = image.OutputDuration,
        OutputFps = image.OutputFps
    };

    private SpeechBubbleData CaptureBubbleData(SpeechBubble bubble)
    {
        var position = GetBubblePositionInOwnerPanel(bubble);
        return new SpeechBubbleData
        {
            Id = bubble.Id,
            Text = bubble.TextBlock.Text,
            X = position.X,
            Y = position.Y,
            Width = bubble.Container.Width,
            Height = bubble.Container.Height,
            FontSize = bubble.MaxFontSize,
            FontFamily = bubble.TextBlock.FontFamily?.Source ?? string.Empty,
            TextAlignment = bubble.TextBlock.TextAlignment.ToString(),
            VerticalAlignment = bubble.TextBlock.VerticalAlignment.ToString(),
            Runs = (bubble.TextBlock.StyledRuns ?? new System.Collections.Generic.List<FlowTextRun>()).Select(r => r.Clone()).ToList(),
            LineHeight = bubble.TextBlock.LineHeight,
            TextMarginLeft = bubble.TextBlock.Margin.Left,
            TextMarginTop = bubble.TextBlock.Margin.Top,
            TextMarginRight = bubble.TextBlock.Margin.Right,
            TextMarginBottom = bubble.TextBlock.Margin.Bottom,
            IsCropped = bubble.IsCropped,
            IsLocked = bubble.IsLocked,
            // 아웃라인 유무는 색의 불투명도로 판단(별도 ON/OFF 없음).
            HasTextOutline = bubble.TextBlock.Stroke is SolidColorBrush sb && sb.Color.A > 0,
            FillColor = ToHex(bubble.TextBlock.Fill),
            StrokeColor = ToHex(bubble.TextBlock.Stroke),
            BackgroundColor = ToHex(bubble.BackgroundBrush),
            BorderColor = ColorToHex(bubble.BorderColor),
            Shape = bubble.Shape.ToString(),
            ShapeCount = bubble.ShapeCount,
            ShapeStrength = bubble.ShapeStrength,
            ShapeIrregularity = bubble.ShapeIrregularity,
            ShapeWidthVariation = bubble.ShapeWidthVariation,
            LineFadeBothSides = bubble.LineFadeBothSides,
            CornerOffsets = PanelOffsetsToArray(bubble.CornerOffsets),
            WarpShape = bubble.WarpShape,
            WarpText = bubble.WarpText,
            TextRotation = bubble.TextRotation,
            PivotX = bubble.PivotX,
            PivotY = bubble.PivotY,
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
                    TailInward = tail.TailInward,
                    ThoughtTail = tail.ThoughtTail
                })
                .ToList()
        };
    }

    // 칸 테두리색을 4개 변 선에 적용한다.
    private static void SetPanelBorderColor(ComicPanel panel, Color color)
    {
        panel.BorderColor = color;
        var brush = new SolidColorBrush(color);
        foreach (var line in panel.QuadBorderLines)
        {
            line.Stroke = brush;
        }
    }

    private ComicPanelData CapturePanelData(ComicPanel panel)
    {
        var panelData = new ComicPanelData
        {
            Number = panel.Number,
            Id = panel.Id,
            Name = panel.Name,
            X = GetCanvasLeft(panel.Frame),
            Y = GetCanvasTop(panel.Frame),
            Width = panel.Frame.Width,
            Height = panel.Frame.Height,
            IsLocked = panel.IsLocked,
            CornerMode = panel.CornerMode,
            BackgroundColor = ColorToHex((panel.QuadFill.Fill as SolidColorBrush)?.Color ?? Colors.White),
            BorderColor = ColorToHex(panel.BorderColor),
            CornerOffsets = PanelOffsetsToArray(panel.CornerOffsets)
        };

        foreach (var image in panel.Images)
        {
            panelData.Images.Add(CaptureImageData(image));
        }

        // 파일이 없어 못 만든 이미지의 원본 데이터를 다시 포함시킨다(저장이 이를 덮어써 잃지 않도록).
        foreach (var unresolved in panel.UnresolvedImages)
        {
            panelData.Images.Add(unresolved);
        }

        foreach (var bubble in panel.Bubbles)
        {
            panelData.Bubbles.Add(CaptureBubbleData(bubble));
        }

        return panelData;
    }

    private List<ComicPageData> CaptureProjectPages(string? projectDirectory)
    {
        SaveCurrentPageState();
        var copiedPages = new List<ComicPageData>();
        foreach (var page in _pages)
        {
            copiedPages.Add(CopyPageForStorage(page, projectDirectory));
        }

        return copiedPages;
    }

    // 페이지 한 장을 저장용으로 깊은 복사한다(이미지 경로를 projectDirectory 기준 상대/절대로 변환).
    // 저장·자동저장·실행취소가 모두 이 한 곳을 거쳐 동일한 직렬화 형식을 보장한다.
    private ComicPageData CopyPageForStorage(ComicPageData page, string? projectDirectory)
    {
        var copiedPage = new ComicPageData
        {
            Name = page.Name,
            PageWidth = page.PageWidth,
            PageHeight = page.PageHeight,
            BackgroundColor = page.BackgroundColor,
            BlackBackground = page.BlackBackground
        };

        foreach (var panel in page.Panels)
        {
            var copiedPanel = new ComicPanelData
            {
                Number = panel.Number,
                Id = panel.Id,
                Name = panel.Name,
                X = panel.X,
                Y = panel.Y,
                Width = panel.Width,
                Height = panel.Height,
                IsLocked = panel.IsLocked,
                CornerMode = panel.CornerMode,
                BackgroundColor = panel.BackgroundColor,
                BorderColor = panel.BorderColor,
                CornerOffsets = (double[])panel.CornerOffsets.Clone(),
                Bubbles = panel.Bubbles
            };

            foreach (var image in panel.Images)
            {
                copiedPanel.Images.Add(new PanelImageData
                {
                    Id = image.Id,
                    Path = MakeStorablePath(image.Path, projectDirectory),
                    Scale = image.Scale,
                    ScaleY = image.ScaleY,
                    TranslateX = image.TranslateX,
                    TranslateY = image.TranslateY,
                    BaseWidth = image.BaseWidth,
                    BaseHeight = image.BaseHeight,
                    IsCropped = image.IsCropped,
                    IsLocked = image.IsLocked,
                    PivotX = image.PivotX,
                    PivotY = image.PivotY,
                    GradientDirection = image.GradientDirection,
                    GradientColor = image.GradientColor,
                    GradientStart = image.GradientStart,
                    GradientEnd = image.GradientEnd,
                    OutputDuration = image.OutputDuration,
                    OutputFps = image.OutputFps
                });
            }

            copiedPage.Panels.Add(copiedPanel);
        }

        return copiedPage;
    }

    private void LoadPage(ComicPageData page)
    {
        ClearPageVisuals();
        _nextPanelNumber = 1;
        // 페이지마다 개별 크기를 적용한다(입력칸도 갱신).
        SetPageSize(page.PageWidth, page.PageHeight, true);
        // 페이지별 배경색 적용. BackgroundColor가 비었으면 구버전 BlackBackground(검/흰)로 본다.
        var pageBg = !string.IsNullOrEmpty(page.BackgroundColor)
            ? page.BackgroundColor
            : (page.BlackBackground ? "#000000" : "#FFFFFF");
        _suppressColorCombo = true;
        SelectBubbleColorInCombo(PageBackgroundColorComboBox, new SolidColorBrush(SafeColor(pageBg)));
        _suppressColorCombo = false;
        ApplyPageBackground();

        foreach (var panelData in page.Panels)
        {
            CreatePanelFromData(panelData);
            _nextPanelNumber = Math.Max(_nextPanelNumber, panelData.Number + 1);
        }

        // 페이지를 열거나 넘어갈 때는 칸을 자동 선택하지 않고 모든 선택을 해제한다.
        ClearSelection();

        UpdateLayoutSummary();
        UpdatePageIndicator();
        // 레이아웃이 갱신된 뒤(스크롤 위치 반영) 현재 뷰포트 기준으로 컬링.
        Dispatcher.BeginInvoke(new Action(CullOffscreenPanels), System.Windows.Threading.DispatcherPriority.Loaded);
        // 인접 페이지(다음·이전) 정지 이미지를 백그라운드로 미리 디코드해 캐시를 데운다(이전 예열은 취소).
        BeginPrefetchAdjacentPages();

        // 파일이 없어 못 띄운 이미지가 있으면 알린다(데이터는 보관되어 저장 시 유지됨 — 파일 복구 후 다시 열면 되살아남).
        var missing = _panels.Sum(p => p.UnresolvedImages.Count);
        if (missing > 0)
        {
            UpdateStatus($"이미지 파일 {missing}개를 찾지 못해 표시하지 못했습니다(데이터는 보존됨 — 파일을 복구한 뒤 다시 열면 복원됩니다).");
        }
    }

    // --- DTO로부터 런타임 오브젝트 생성(불러오기·붙여넣기 공용) ---

    private PanelImage? AddImageFromData(ComicPanel panel, PanelImageData imageData)
    {
        var imagePath = ResolveProjectPath(imageData.Path);
        if (!File.Exists(imagePath))
        {
            // 파일이 없어도 데이터를 버리지 않고 보관한다(저장 시 다시 합쳐 영구 삭제 방지).
            panel.UnresolvedImages.Add(imageData);
            return null;
        }

        var image = AddPanelImage(panel, imagePath);
        if (!string.IsNullOrEmpty(imageData.Id)) image.Id = imageData.Id; // 저장/실행취소 ID 유지(붙여넣기는 비어 있어 새 ID 유지).
        // 기준 콘텐츠 박스 크기 복원: Scale·Translate가 이 박스를 기준으로 계산되므로 추가 당시 크기로 되돌려야
        // 위치/크기가 보존된다. 저장값이 없거나(구버전) 유효하지 않으면 현재 칸 크기(AddPanelImage 기본값)를 유지한다.
        if (IsValidBaseSize(imageData.BaseWidth) && IsValidBaseSize(imageData.BaseHeight))
        {
            ApplyImageBaseSize(image, imageData.BaseWidth, imageData.BaseHeight);
        }
        image.Scale.ScaleX = imageData.Scale <= 0 ? 1 : imageData.Scale;
        // ScaleY 미지정(구버전/비율 유지)이면 Scale과 동일(균일).
        image.Scale.ScaleY = imageData.ScaleY <= 0
            ? (imageData.Scale <= 0 ? 1 : imageData.Scale)
            : imageData.ScaleY;
        image.Translate.X = imageData.TranslateX;
        image.Translate.Y = imageData.TranslateY;
        image.PivotX = imageData.PivotX;
        image.PivotY = imageData.PivotY;
        image.GradientDirection = ParseGradientDirection(imageData.GradientDirection);
        image.GradientColor = SafeColor(imageData.GradientColor);
        image.GradientStart = imageData.GradientStart;
        image.GradientEnd = imageData.GradientEnd;
        image.OutputDuration = imageData.OutputDuration;
        image.OutputFps = imageData.OutputFps;
        ApplyImageGradient(image);
        SetImageCrop(image, imageData.IsCropped);
        SetImageLocked(image, imageData.IsLocked);
        ApplyImageOutputTiming(image); // 저장된 출력 설정대로 라이브 재생 타이밍 적용.
        return image;
    }

    private static bool IsValidBaseSize(double v) => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v);

    // 이미지 리그(레이어 + 그 안의 콘텐츠·그라데이션·선택틴트·동영상 포스터)를 모두 같은 크기로 맞춘다.
    // 모든 요소가 동일 크기 + 동일 변환 + 원점(0.5,0.5)을 공유해야 정렬이 맞으므로 함께 바꾼다.
    private static void ApplyImageBaseSize(PanelImage image, double w, double h)
    {
        image.Layer.Width = w;
        image.Layer.Height = h;
        foreach (var child in image.Layer.Children)
        {
            if (child is FrameworkElement fe)
            {
                fe.Width = w;
                fe.Height = h;
            }
        }
    }

    private SpeechBubble AddBubbleFromData(ComicPanel panel, SpeechBubbleData bubbleData)
    {
        var bubble = CreateSpeechBubble(
            panel,
            bubbleData.Text,
            bubbleData.Width,
            bubbleData.Height,
            bubbleData.FontSize,
            bubbleData.X,
            bubbleData.Y);

        if (!string.IsNullOrEmpty(bubbleData.Id)) bubble.Id = bubbleData.Id; // 저장/실행취소 ID 유지(붙여넣기는 새 ID).
        // 글꼴 복원(구버전/미지정이면 기존 기본 글꼴).
        bubble.TextBlock.FontFamily = new FontFamily(string.IsNullOrEmpty(bubbleData.FontFamily) ? "Malgun Gothic" : bubbleData.FontFamily);
        // 텍스트 정렬 복원(구버전/미지정이면 가운데).
        bubble.TextBlock.TextAlignment = ParseFlowAlignment(string.IsNullOrEmpty(bubbleData.TextAlignment) ? "Center" : bubbleData.TextAlignment);
        bubble.TextBlock.VerticalAlignment = ParseVerticalAlignment(string.IsNullOrEmpty(bubbleData.VerticalAlignment) ? "Center" : bubbleData.VerticalAlignment);
        // 구간별 서식 복원(구버전/미지정이면 Text 한 덩어리로 마이그레이션). 측정·렌더가 이를 쓰므로 먼저 설정.
        bubble.TextBlock.StyledRuns = bubbleData.Runs is { Count: > 0 }
            ? bubbleData.Runs.Select(r => r.Clone()).ToList()
            : MakeDefaultRuns(bubbleData.Text);
        bubble.BorderColor = SafeColor(string.IsNullOrEmpty(bubbleData.BorderColor) ? "#000000" : bubbleData.BorderColor);
        bubble.TextBlock.Margin = new Thickness(bubbleData.TextMarginLeft, bubbleData.TextMarginTop, bubbleData.TextMarginRight, bubbleData.TextMarginBottom);
        bubble.TextBlock.LineHeight = bubbleData.LineHeight;

        var (mappedShape, legacyStrength) = MapShape(bubbleData.Shape);
        bubble.Shape = mappedShape;
        bubble.ShapeStrength = legacyStrength ?? bubbleData.ShapeStrength;
        bubble.ShapeCount = bubbleData.ShapeCount <= 0 ? 9 : bubbleData.ShapeCount;
        // 0/미지정(구버전)이면 기존 기본 흔들림 50으로 본다.
        bubble.ShapeIrregularity = bubbleData.ShapeIrregularity <= 0 ? 50 : bubbleData.ShapeIrregularity;
        bubble.ShapeWidthVariation = bubbleData.ShapeWidthVariation;
        bubble.LineFadeBothSides = bubbleData.LineFadeBothSides;
        ApplyArrayToPanelOffsets(bubbleData.CornerOffsets, bubble.CornerOffsets);
        bubble.WarpShape = bubbleData.WarpShape;
        bubble.TextRotation = bubbleData.TextRotation;
        bubble.WarpText = bubbleData.WarpText;
        bubble.PivotX = bubbleData.PivotX;
        bubble.PivotY = bubbleData.PivotY;
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
            TailInward = tail.TailInward || bubbleData.TailInward,
            ThoughtTail = tail.ThoughtTail
        }));
        UpdateBubbleGeometry(bubble);

        AttachBubbleToPanelOverlay(bubble);
        if (!bubbleData.IsCropped)
        {
            SetBubbleCrop(bubble, false);
        }

        SetBubbleLocked(bubble, bubbleData.IsLocked);
        bubble.TextBlock.Fill = new SolidColorBrush(ParseColorOr(bubbleData.FillColor, Colors.Black));
        // 아웃라인 OFF였던(또는 새 모델의 투명) 말풍선은 투명 아웃라인으로 — 색이 있어도 안 보이게.
        bubble.TextBlock.Stroke = bubbleData.HasTextOutline
            ? new SolidColorBrush(ParseColorOr(bubbleData.StrokeColor, Colors.White))
            : Brushes.Transparent;
        bubble.BackgroundBrush = new SolidColorBrush(ParseColorOr(bubbleData.BackgroundColor, Colors.White));
        bubble.ShapePath.Fill = bubble.BackgroundBrush;
        panel.Bubbles.Add(bubble);
        return bubble;
    }

    private ComicPanel CreatePanelFromData(ComicPanelData panelData, double? overrideX = null, double? overrideY = null, int? overrideNumber = null)
    {
        var panel = CreatePanel(
            overrideNumber ?? panelData.Number,
            overrideX ?? panelData.X,
            overrideY ?? panelData.Y,
            panelData.Width,
            panelData.Height);
        if (!string.IsNullOrEmpty(panelData.Id)) panel.Id = panelData.Id; // 저장/실행취소 ID 유지(붙여넣기는 새 ID).
        panel.Name = panelData.Name ?? string.Empty;
        panel.QuadFill.Fill = new SolidColorBrush(SafeColor(string.IsNullOrEmpty(panelData.BackgroundColor) ? "#FFFFFF" : panelData.BackgroundColor));
        SetPanelBorderColor(panel, SafeColor(string.IsNullOrEmpty(panelData.BorderColor) ? "#000000" : panelData.BorderColor));
        AddPanel(panel);

        foreach (var imageData in panelData.Images)
        {
            AddImageFromData(panel, imageData);
        }

        foreach (var bubbleData in panelData.Bubbles)
        {
            AddBubbleFromData(panel, bubbleData);
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
        return panel;
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
        // 호버 강조 대상은 방금 파괴됐으니 참조를 끊는다(다음 마우스 이동에서 재설정).
        _hoveredObject = null;
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

        // ItemsSource를 _pages(ObservableCollection)에 한 번만 연결한다. 이후 추가/삭제/이동은
        // 컬렉션 변경 이벤트로 '바뀐 항목만' 증분 갱신되므로 여기서 목록을 다시 만들지 않는다.
        if (!ReferenceEquals(PageListBox.ItemsSource, _pages))
        {
            PageListBox.ItemsSource = _pages;
        }

        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            page.IsEditing = false; // 목록 갱신 시 인라인 편집 모드 해제(객체 bool만, 컨테이너 재생성 아님).
            page.VisualNovelMode = _flow.Enabled; // 모드에 따라 목록 라벨(이름↔말풍선 요약) 전환.
            page.DisplayIndex = i + 1; // 위치 기반 순번(가상화·스크롤과 무관하게 정확).
        }

        PageListBox.SelectedIndex = _currentPageIndex;
        _isUpdatingPageList = false;
        UpdatePageIndicator();
    }

    // 불러오기/복원처럼 전체를 갈아끼울 때 사용(ObservableCollection엔 AddRange가 없음).
    // Clear(Reset) 후 한 건씩 추가 — 전체 교체라 증분 이점은 없지만 드물게만 일어난다.
    private void ReplacePages(IEnumerable<ComicPageData> pages)
    {
        _editingTemplate = null; // 페이지 전체 교체 시 템플릿 편집 모드 해제.
        _pages.Clear();
        foreach (var page in pages)
        {
            _pages.Add(page);
        }
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
        CullOffscreenPanels();
    }

    // 뷰포트(±한 화면) 밖 칸을 Collapsed로 만들어, 긴 페이지에서도 렌더·레이아웃·히트테스트 비용을 일정하게 유지한다.
    // 칸 한 개의 모든 콘텐츠가 Frame 하나에 들어 있어 Frame.Visibility만 토글하면 된다.
    private void CullOffscreenPanels()
    {
        if (PageScrollViewer == null || PanelCanvas == null)
        {
            return;
        }

        // 드래그 중에는 레이아웃을 건드리지 않는다(드래그 대상이 사라지는 것 방지).
        if (_isDraggingPanel || _isDraggingPanelImage || _isDraggingBubble)
        {
            return;
        }

        Rect visible;
        try
        {
            var t = PageScrollViewer.TransformToDescendant(PanelCanvas);
            var tl = t.Transform(new Point(0, 0));
            var br = t.Transform(new Point(PageScrollViewer.ViewportWidth, PageScrollViewer.ViewportHeight));
            visible = new Rect(tl, br);
        }
        catch
        {
            foreach (var p in _panels)
            {
                p.Frame.Visibility = Visibility.Visible;
            }
            return;
        }

        // 위/아래 한 화면만큼 여유를 둬 스크롤 시 빈 공간이 잠깐 보이는 것을 막는다.
        var marginY = Math.Max(200, visible.Height);
        var top = visible.Top - marginY;
        var bottom = visible.Bottom + marginY;

        foreach (var panel in _panels)
        {
            // 크롭 OFF 이미지·말풍선은 칸 밖으로 넘치므로, 프레임이 아니라 콘텐츠 전체 경계로 판정한다.
            var cb = GetPanelCullBoundsPage(panel);
            var onScreen = cb.Bottom >= top && cb.Top <= bottom;
            // 선택된 칸/말풍선/이미지의 소속 칸은 화면 밖이라도 유지(리스트 선택·편집 중).
            var selected = ReferenceEquals(panel, _selectedPanel)
                || ReferenceEquals(panel, _selectedBubble?.OwnerPanel)
                || ReferenceEquals(panel, _selectedImage?.OwnerPanel);
            var target = onScreen || selected ? Visibility.Visible : Visibility.Collapsed;
            if (panel.Frame.Visibility != target)
            {
                panel.Frame.Visibility = target;
            }
        }
    }

    // 칸의 페이지 좌표 경계(크롭 OFF 이미지·말풍선의 칸 밖 넘침 포함). 레이아웃에 의존하지 않고 트랜스폼/위치 데이터로 계산한다.
    private Rect GetPanelCullBoundsPage(ComicPanel panel)
    {
        var fl = GetCanvasLeft(panel.Frame);
        var ft = GetCanvasTop(panel.Frame);
        if (double.IsNaN(fl)) fl = 0;
        if (double.IsNaN(ft)) ft = 0;
        var bounds = new Rect(fl, ft, panel.Frame.Width, panel.Frame.Height);

        // 크롭 OFF 이미지: 칸 밖으로 넘치는 실제 표시 영역.
        foreach (var img in panel.Images)
        {
            if (img.IsCropped)
            {
                continue; // 크롭 ON은 칸 사변형으로 클립되어 프레임 안.
            }

            bounds.Union(GetImageVisiblePageBounds(img));
        }

        // 크롭 OFF 말풍선: 컨테이너 + 꼬리 끝점까지.
        foreach (var b in panel.Bubbles)
        {
            if (b.IsCropped)
            {
                continue;
            }

            var bx = fl + GetCanvasLeft(b.Container);
            var by = ft + GetCanvasTop(b.Container);
            if (double.IsNaN(bx)) bx = fl;
            if (double.IsNaN(by)) by = ft;
            bounds.Union(new Rect(bx, by, b.Container.Width, b.Container.Height));
            foreach (var tail in b.Tails)
            {
                bounds.Union(new Point(bx + tail.StartX, by + tail.StartY));
                bounds.Union(new Point(bx + tail.MidX, by + tail.MidY));
                bounds.Union(new Point(bx + tail.X, by + tail.Y));
            }
        }

        return bounds;
    }

    // 오브젝트(칸/이미지/말풍선)용 새 고유 ID. 실행취소/다시실행 후 같은 오브젝트를 다시 선택하는 데 쓴다.
    private static string NewObjectId() => Guid.NewGuid().ToString("N");

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
            Fill = new SolidColorBrush(Colors.White), // 칸 배경색(인스펙터에서 변경 가능). 기본 흰색.
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
        // 칸 선택 히트 영역을 프레임보다 바깥으로 조금 넓히는 투명 영역(맨 뒤에 깔아 콘텐츠 클릭은 방해하지 않는다).
        // 프레임의 자식이라 클릭이 BeginPanelDrag로 라우팅되고, 바깥 밴드는 IsOnPanelBorder가 테두리로 인정한다.
        grid.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = Brushes.Transparent,
            Margin = new Thickness(-PanelOutwardHitMargin)
        });
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
            FreeImageCanvas = freeImageCanvas,
            Id = NewObjectId()
        };
        Canvas.SetLeft(frame, ClampPanelX(x, width));
        Canvas.SetTop(frame, ClampPanelY(y, height));

        frame.PreviewMouseLeftButtonDown += (_, e) => BeginPanelDrag(panel, e);
        frame.PreviewMouseMove += (_, e) => DragPanel(panel, e);
        frame.PreviewMouseLeftButtonUp += (_, e) => EndPanelDrag(panel, e);
        frame.LostMouseCapture += (_, _) => ResetDragState();
        frame.DragOver += (_, e) => DragOverPanel(panel, e);
        frame.Drop += (_, e) => DropImageOnPanel(panel, e);
        resizeHandle.DragStarted += (_, _) => SelectPanel(panel);
        resizeHandle.DragDelta += (_, e) => ResizePanel(panel, e);

        // 새로 생성하는 칸은 기본 잠금 OFF(불러오기 시에는 LoadPage에서 저장된 상태로 덮어쓴다).
        SetPanelLocked(panel, false);

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
            FontFamily = new FontFamily(_lastBubbleFontFamily),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Fill = Brushes.Black,
            Stroke = Brushes.Transparent, // 기본 아웃라인 없음(투명). 색을 고르면 아웃라인이 생긴다.
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
            // 새 말풍선 기본 모양은 원형(RoundRect+강도0=타원). 불러오기·붙여넣기는 이후 실제 모양으로 덮어쓴다.
            Shape = BubbleShape.RoundRect,
            LineHost = lineHost,
            MaxFontSize = fontSize,
            Id = NewObjectId()
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
}
