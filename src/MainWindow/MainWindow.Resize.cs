using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KomaForge;

public partial class MainWindow : Window
{
    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    // 8방향 핸들 — 인덱스 고정: 0=TL,1=T,2=TR,3=L,4=R,5=BL,6=B,7=BR
    private static readonly ResizeEdge[] ResizeEdges =
    {
        ResizeEdge.Top | ResizeEdge.Left,
        ResizeEdge.Top,
        ResizeEdge.Top | ResizeEdge.Right,
        ResizeEdge.Left,
        ResizeEdge.Right,
        ResizeEdge.Bottom | ResizeEdge.Left,
        ResizeEdge.Bottom,
        ResizeEdge.Bottom | ResizeEdge.Right,
    };

    private Thumb[]? _bubbleResizeHandles;
    private Thumb[]? _panelResizeHandles;
    private Thumb[]? _imageResizeHandles;

    private const double MinImageSize = 20;

    private static Cursor CursorForEdge(ResizeEdge e)
    {
        var horiz = e.HasFlag(ResizeEdge.Left) || e.HasFlag(ResizeEdge.Right);
        var vert = e.HasFlag(ResizeEdge.Top) || e.HasFlag(ResizeEdge.Bottom);
        if (horiz && vert)
        {
            var nwse = (e.HasFlag(ResizeEdge.Left) && e.HasFlag(ResizeEdge.Top)) ||
                       (e.HasFlag(ResizeEdge.Right) && e.HasFlag(ResizeEdge.Bottom));
            return nwse ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }

        return horiz ? Cursors.SizeWE : Cursors.SizeNS;
    }

    private static Thumb CreateResizeHandle(ResizeEdge edge)
    {
        return new Thumb
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = CursorForEdge(edge),
            Background = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Hidden
        };
    }

    // 드래그한 엣지에 맞춰 새 사각형 계산(min 보정 포함). 좌/상 엣지는 위치(X/Y)도 함께 바뀐다.
    private static Rect ApplyResize(ResizeEdge edge, Rect b, double dx, double dy, double minW, double minH)
    {
        double x = b.X, y = b.Y, w = b.Width, h = b.Height;

        if (edge.HasFlag(ResizeEdge.Left))
        {
            var nw = w - dx;
            if (nw < minW) { dx = w - minW; nw = minW; }
            x += dx;
            w = nw;
        }
        else if (edge.HasFlag(ResizeEdge.Right))
        {
            w = Math.Max(minW, w + dx);
        }

        if (edge.HasFlag(ResizeEdge.Top))
        {
            var nh = h - dy;
            if (nh < minH) { dy = h - minH; nh = minH; }
            y += dy;
            h = nh;
        }
        else if (edge.HasFlag(ResizeEdge.Bottom))
        {
            h = Math.Max(minH, h + dy);
        }

        return new Rect(x, y, w, h);
    }

    private static bool ShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    // 이미지: 환경설정 옵션이 켜져 있거나 Shift를 누르면 비율 유지. (칸·말풍선은 Shift만으로 판정한다.)
    private bool KeepAspectForImage() => _keepAspectRatio || ShiftHeld;

    // 드래그한 엣지에 맞춰 새 사각형 계산. keepAspect면 aspect(가로/세로) 비율을 유지한다.
    private Rect ComputeResizedBounds(Rect b, ResizeEdge edge, double dx, double dy, double minW, double minH, bool keepAspect, double aspect)
    {
        if (!keepAspect || aspect <= 0 || b.Width <= 0 || b.Height <= 0)
        {
            return ApplyResize(edge, b, dx, dy, minW, minH);
        }

        var horiz = edge.HasFlag(ResizeEdge.Left) || edge.HasFlag(ResizeEdge.Right);
        var vert = edge.HasFlag(ResizeEdge.Top) || edge.HasFlag(ResizeEdge.Bottom);

        double fw, fh;
        if (horiz && vert)
        {
            // 모서리: 앵커(반대 모서리)→끈 모서리 대각선 방향에 마우스 이동을 투영해 균일 배율(부드럽게).
            var diagX = edge.HasFlag(ResizeEdge.Right) ? b.Width : -b.Width;
            var diagY = edge.HasFlag(ResizeEdge.Bottom) ? b.Height : -b.Height;
            var denom = diagX * diagX + diagY * diagY;
            var f = denom > 0 ? 1.0 + (dx * diagX + dy * diagY) / denom : 1.0;
            fw = b.Width * f;
            fh = fw / aspect;
        }
        else if (horiz)
        {
            fw = ApplyResize(edge, b, dx, 0, minW, minH).Width;
            fh = fw / aspect;
        }
        else
        {
            fh = ApplyResize(edge, b, 0, dy, minW, minH).Height;
            fw = fh * aspect;
        }

        // 최소 크기 보장(비율 유지).
        if (fw < minW) { fw = minW; fh = fw / aspect; }
        if (fh < minH) { fh = minH; fw = fh * aspect; }

        // 반대 변/모서리를 앵커로 고정(단일 변은 수직축 가운데 고정).
        double nx;
        if (edge.HasFlag(ResizeEdge.Left)) nx = b.Right - fw;
        else if (edge.HasFlag(ResizeEdge.Right)) nx = b.Left;
        else nx = b.X + (b.Width - fw) / 2.0;

        double ny;
        if (edge.HasFlag(ResizeEdge.Top)) ny = b.Bottom - fh;
        else if (edge.HasFlag(ResizeEdge.Bottom)) ny = b.Top;
        else ny = b.Y + (b.Height - fh) / 2.0;

        return new Rect(nx, ny, fw, fh);
    }

    // 8개 핸들을 사각형(페이지 좌표)의 8지점에 배치한다.
    private static void PlaceResizeHandles(Thumb[] handles, double x, double y, double w, double h, Brush accent)
    {
        var px = new[] { x, x + w / 2, x + w, x, x + w, x, x + w / 2, x + w };
        var py = new[] { y, y, y, y + h / 2, y + h / 2, y + h, y + h, y + h };
        for (var i = 0; i < 8; i++)
        {
            handles[i].Background = accent;
            Canvas.SetLeft(handles[i], px[i] - handles[i].Width / 2);
            Canvas.SetTop(handles[i], py[i] - handles[i].Height / 2);
        }
    }

    // --- 말풍선 8방향 리사이즈 핸들 ---

    private void EnsureBubbleResizeHandles()
    {
        if (_bubbleResizeHandles == null)
        {
            _bubbleResizeHandles = new Thumb[8];
            for (var i = 0; i < 8; i++)
            {
                var edge = ResizeEdges[i];
                var handle = CreateResizeHandle(edge);
                handle.DragStarted += (_, _) =>
                {
                    if (_selectedBubble == null) return;
                    SelectBubble(_selectedBubble);
                    var h = _selectedBubble.Container.Height;
                    _resizeStartAspect = h > 0 ? _selectedBubble.Container.Width / h : 1;
                };
                handle.DragDelta += (_, e) => { if (_selectedBubble != null) ResizeBubbleEdge(_selectedBubble, edge, e); };
                handle.DragCompleted += (_, _) => ClearSnapGuides();
                _bubbleResizeHandles[i] = handle;
            }
        }

        foreach (var handle in _bubbleResizeHandles)
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    private void PositionBubbleResizeHandles(bool show, double x, double y, double w, double h, Brush accent)
    {
        EnsureBubbleResizeHandles();
        foreach (var handle in _bubbleResizeHandles!)
        {
            handle.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        }

        if (show)
        {
            PlaceResizeHandles(_bubbleResizeHandles, x, y, w, h, accent);
        }
    }

    private void ResizeBubbleEdge(SpeechBubble bubble, ResizeEdge edge, DragDeltaEventArgs e)
    {
        SelectBubble(bubble);

        var pos = GetBubblePositionInOwnerPanel(bubble);
        var oldWidth = bubble.Container.Width;
        var oldHeight = bubble.Container.Height;
        var rect = new Rect(pos.X, pos.Y, oldWidth, oldHeight);
        var nb = ComputeResizedBounds(rect, edge, e.HorizontalChange, e.VerticalChange,
            BubbleWidthSlider.Minimum, BubbleHeightSlider.Minimum, ShiftHeld, _resizeStartAspect);

        // 소유 칸의 변에 스냅(Alt로 일시 해제).
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            nb = SnapBubbleBounds(bubble, edge, nb, ShiftHeld);
        }
        else
        {
            ClearSnapGuides();
        }

        bubble.Container.Width = nb.Width;
        bubble.Container.Height = nb.Height;
        SetBubblePositionInOwnerPanel(bubble, nb.X, nb.Y);

        // 박스 크기 변화 비율에 맞춰 꼬리(시작·중간·끝점)도 함께 이동(휠 확대축소와 동일).
        if (oldWidth > 0 && oldHeight > 0)
        {
            var ratioW = nb.Width / oldWidth;
            var ratioH = nb.Height / oldHeight;
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

        _isLoadingInspector = true;
        BubbleWidthSlider.Value = Math.Clamp(nb.Width, BubbleWidthSlider.Minimum, BubbleWidthSlider.Maximum);
        BubbleHeightSlider.Value = Math.Clamp(nb.Height, BubbleHeightSlider.Minimum, BubbleHeightSlider.Maximum);
        BubbleXSlider.Value = Math.Clamp(nb.X, BubbleXSlider.Minimum, BubbleXSlider.Maximum);
        BubbleYSlider.Value = Math.Clamp(nb.Y, BubbleYSlider.Minimum, BubbleYSlider.Maximum);
        _isLoadingInspector = false;

        UpdateBubbleGeometry(bubble);
        UpdateInspectorLabels();
        PositionBubbleSelectionBox();
    }

    // --- 칸 8방향 리사이즈 핸들 ---

    private void EnsurePanelResizeHandles()
    {
        if (_panelResizeHandles == null)
        {
            _panelResizeHandles = new Thumb[8];
            for (var i = 0; i < 8; i++)
            {
                var edge = ResizeEdges[i];
                var handle = CreateResizeHandle(edge);
                handle.DragStarted += (_, _) =>
                {
                    if (_selectedPanel == null) return;
                    SelectPanel(_selectedPanel);
                    var h = _selectedPanel.Frame.Height;
                    _resizeStartAspect = h > 0 ? _selectedPanel.Frame.Width / h : 1;
                };
                handle.DragDelta += (_, e) => { if (_selectedPanel != null) ResizePanelEdge(_selectedPanel, edge, e); };
                handle.DragCompleted += (_, _) => ClearSnapGuides();
                _panelResizeHandles[i] = handle;
            }
        }

        foreach (var handle in _panelResizeHandles)
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    // 칸 선택 박스(말풍선처럼 PageOverlay에 올려 항상 맨 앞·비클리핑). 칸 자체 테두리와 별개.
    private void EnsurePanelSelectionBox()
    {
        if (_panelSelectionBox == null)
        {
            _panelSelectionBox = new Border
            {
                BorderBrush = SelectionAccentBrush,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };
        }

        // 페이지 재구성(실행취소·페이지 전환)이 PageOverlay를 비우므로, 빠졌으면 다시 넣는다.
        if (!PageOverlay.Children.Contains(_panelSelectionBox))
        {
            PageOverlay.Children.Add(_panelSelectionBox);
            Panel.SetZIndex(_panelSelectionBox, int.MaxValue - 2);
        }
    }

    // 칸 선택 시 호출(잠금/사변형 모드가 아닐 때만 8방향 핸들 + 선택 박스 표시. 사변형 모드는 모서리 핸들이 담당).
    private void PositionPanelResizeHandles()
    {
        EnsurePanelResizeHandles();
        EnsurePanelSelectionBox();

        var show = _selectionKind == SelectionKind.Panel && _selectedPanel != null
                   && !_selectedPanel.IsLocked && !_selectedPanel.CornerMode;
        foreach (var handle in _panelResizeHandles!)
        {
            handle.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        }

        _panelSelectionBox!.Visibility = show ? Visibility.Visible : Visibility.Hidden;

        if (!show || _selectedPanel == null)
        {
            return;
        }

        var x = GetCanvasLeft(_selectedPanel.Frame);
        var y = GetCanvasTop(_selectedPanel.Frame);
        var w = _selectedPanel.Frame.Width;
        var h = _selectedPanel.Frame.Height;
        PlaceResizeHandles(_panelResizeHandles, x, y, w, h, SelectionAccentBrush);

        Canvas.SetLeft(_panelSelectionBox, x);
        Canvas.SetTop(_panelSelectionBox, y);
        _panelSelectionBox.Width = w;
        _panelSelectionBox.Height = h;
    }

    private void ResizePanelEdge(ComicPanel panel, ResizeEdge edge, DragDeltaEventArgs e)
    {
        SelectPanel(panel);

        var keepAspect = ShiftHeld;
        var rect = new Rect(GetCanvasLeft(panel.Frame), GetCanvasTop(panel.Frame), panel.Frame.Width, panel.Frame.Height);
        var nb = ComputeResizedBounds(rect, edge, e.HorizontalChange, e.VerticalChange,
            PanelWidthSlider.Minimum, PanelHeightSlider.Minimum, keepAspect, _resizeStartAspect);

        // 여백/간격·다른 칸 모서리에 스냅한다(Alt로 끔). 비율 유지(Shift) 중에는 비율을 깨지 않는 스냅을 쓴다.
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            nb = keepAspect ? SnapPanelBoundsKeepAspect(panel, edge, nb) : SnapPanelBounds(panel, edge, nb);
        }
        else
        {
            ClearSnapGuides();
        }

        var dW = nb.Width - panel.Frame.Width;
        var dH = nb.Height - panel.Frame.Height;

        Canvas.SetLeft(panel.Frame, nb.X);
        Canvas.SetTop(panel.Frame, nb.Y);
        panel.Frame.Width = nb.Width;
        panel.Frame.Height = nb.Height;

        ApplyPivotShift(panel, dW, dH);
        UpdatePanelImageSizes(panel);
        LoadPanelValues(panel);
        UpdateFreeBubblesForPanel(panel);
        PositionPanelResizeHandles();
    }

    private const double PanelSnapThreshold = 10;

    // 이미지 이동 시, '실제로 보이는' 이미지 경계를 소유 칸의 4변에 스냅한 새 Translate를 돌려준다.
    // 콘텐츠는 Uniform(레터박스)이라 가시 이미지는 콘텐츠 박스보다 작을 수 있다. 둘 다 중심 정렬이므로
    // 가시 이미지 중심 = 콘텐츠 박스 중심 = (cw/2 + tx, ch/2 + ty).
    private (double X, double Y) SnapImageTranslate(PanelImage image, double tx, double ty)
    {
        var cw = image.Content.Width;
        var ch = image.Content.Height;
        if (cw <= 0 || ch <= 0)
        {
            return (tx, ty);
        }

        var (rw, rh) = GetImageRenderedSize(image);
        var w = rw * image.Scale.ScaleX;
        var h = rh * image.Scale.ScaleY;
        var frame = image.OwnerPanel.Frame;
        var frameLeft = GetCanvasLeft(frame);
        var frameTop = GetCanvasTop(frame);
        // 칸-로컬 좌표 기준. 칸 변(0, frame 크기)에 스냅.
        var xs = new List<double> { 0, frame.Width };
        var ys = new List<double> { 0, frame.Height };

        // 크롭 OFF면 이미지가 페이지 전체에 보이므로 페이지의 완전한 끝(0·페이지 크기)에도 스냅.
        // 페이지 좌표 0은 칸-로컬로 -frameLeft, 페이지 끝은 pageW-frameLeft.
        if (!image.IsCropped)
        {
            xs.Add(-frameLeft);
            xs.Add(_pageWidth - frameLeft);
            ys.Add(-frameTop);
            ys.Add(_pageHeight - frameTop);
        }

        var left = cw / 2 + tx - w / 2;
        var top = ch / 2 + ty - h / 2;
        double? gx = null, gy = null;
        // 스냅 후보·좌표는 칸-로컬이므로, 가이드 표시용으로 페이지 좌표(+frameLeft/Top)로 환산한다.
        if (TrySnapEdgePair(left, left + w, xs, PanelSnapThreshold, out var nl, out var cx)) { left = nl; gx = cx + frameLeft; }
        if (TrySnapEdgePair(top, top + h, ys, PanelSnapThreshold, out var nt, out var cy)) { top = nt; gy = cy + frameTop; }
        ShowSnapGuides(gx, gy);

        // 가시 좌상단(left,top) → Translate 역산. left = cw/2 + tx - w/2.
        return (left + w / 2 - cw / 2, top + h / 2 - ch / 2);
    }

    // 이미지의 '실제로 보이는' 영역(레터박스 제외)의 화면 좌표 경계.
    private static Rect GetImageVisiblePageBounds(PanelImage image)
    {
        var cw = image.Content.Width;
        var ch = image.Content.Height;
        var (rw, rh) = GetImageRenderedSize(image);
        var w = rw * image.Scale.ScaleX;
        var h = rh * image.Scale.ScaleY;
        var cx = GetCanvasLeft(image.OwnerPanel.Frame) + cw / 2.0 + image.Translate.X;
        var cy = GetCanvasTop(image.OwnerPanel.Frame) + ch / 2.0 + image.Translate.Y;
        return new Rect(cx - w / 2.0, cy - h / 2.0, w, h);
    }

    private void EnsureImageSelectionBox()
    {
        if (_imageSelectionBox == null)
        {
            _imageSelectionBox = new Border
            {
                BorderBrush = SelectionAccentBrush,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };
        }

        // 페이지 재구성(실행취소·페이지 전환)이 PageOverlay를 비우므로, 빠졌으면 다시 넣는다.
        if (!PageOverlay.Children.Contains(_imageSelectionBox))
        {
            PageOverlay.Children.Add(_imageSelectionBox);
            Panel.SetZIndex(_imageSelectionBox, int.MaxValue - 2);
        }
    }

    private void EnsureImageResizeHandles()
    {
        if (_imageResizeHandles == null)
        {
            _imageResizeHandles = new Thumb[8];
            for (var i = 0; i < 8; i++)
            {
                var edge = ResizeEdges[i];
                var handle = CreateResizeHandle(edge);
                handle.DragStarted += (_, _) =>
                {
                    if (_selectedImage == null) return;
                    SelectImage(_selectedImage);
                    var b = GetImageVisiblePageBounds(_selectedImage);
                    _resizeStartAspect = b.Height > 0 ? b.Width / b.Height : 1;
                };
                handle.DragDelta += (_, e) => { if (_selectedImage != null) ResizeImageEdge(_selectedImage, edge, e); };
                handle.DragCompleted += (_, _) => ClearSnapGuides();
                _imageResizeHandles[i] = handle;
            }
        }

        foreach (var handle in _imageResizeHandles)
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    // 선택된 이미지의 보이는 영역에 테두리 박스 + 8방향 핸들을 표시. 이동·휠 확대 시에도 호출해 따라가게 한다.
    private void PositionImageSelectionBox()
    {
        EnsureImageSelectionBox();
        EnsureImageResizeHandles();

        var selected = _selectionKind == SelectionKind.Image && _selectedImage != null;
        var showHandles = selected && !_selectedImage!.IsLocked;
        _imageSelectionBox!.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        foreach (var handle in _imageResizeHandles!)
        {
            handle.Visibility = showHandles ? Visibility.Visible : Visibility.Hidden;
        }

        if (!selected || _selectedImage == null)
        {
            return;
        }

        var b = GetImageVisiblePageBounds(_selectedImage);
        var accent = _selectedImage.IsLocked ? SelectionLockedBrush : SelectionAccentBrush;
        // 잠긴 이미지는 빨강으로 구분(칸/말풍선과 동일 규칙).
        _imageSelectionBox.BorderBrush = accent;
        Canvas.SetLeft(_imageSelectionBox, b.X);
        Canvas.SetTop(_imageSelectionBox, b.Y);
        _imageSelectionBox.Width = b.Width;
        _imageSelectionBox.Height = b.Height;

        if (showHandles)
        {
            PlaceResizeHandles(_imageResizeHandles, b.X, b.Y, b.Width, b.Height, accent);
        }
    }

    // 이미지 8방향 리사이즈. 비율 유지(옵션 ON/Shift)면 균일 배율, 아니면 가로/세로 독립(자유) 조절.
    private void ResizeImageEdge(PanelImage image, ResizeEdge edge, DragDeltaEventArgs e)
    {
        SelectImage(image);

        var (rw, rh) = GetImageRenderedSize(image);
        if (rw <= 0 || rh <= 0)
        {
            return;
        }

        var b = GetImageVisiblePageBounds(image);
        if (b.Width <= 0 || b.Height <= 0)
        {
            return;
        }

        var keepAspect = KeepAspectForImage();
        var nb = ComputeResizedBounds(b, edge, e.HorizontalChange, e.VerticalChange,
            MinImageSize, MinImageSize, keepAspect, _resizeStartAspect);

        // 소유 칸 4변에 스냅(Alt로 끔). 비율 유지 시엔 비율을 깨지 않는 균일 배율 스냅을 쓴다.
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            nb = SnapImageBounds(image, edge, nb, keepAspect);
        }
        else
        {
            ClearSnapGuides();
        }

        // 보이는 사각형 → 배율 + Translate 환산. 비율 미유지면 가로/세로 배율이 달라진다(ScaleX≠ScaleY).
        var fl = GetCanvasLeft(image.OwnerPanel.Frame);
        var ft = GetCanvasTop(image.OwnerPanel.Frame);
        var cw = image.Content.Width;
        var ch = image.Content.Height;

        image.Scale.ScaleX = Math.Max(0.02, nb.Width / rw);
        image.Scale.ScaleY = Math.Max(0.02, nb.Height / rh);
        image.Translate.X = (nb.X + nb.Width / 2.0) - fl - cw / 2.0;
        image.Translate.Y = (nb.Y + nb.Height / 2.0) - ft - ch / 2.0;

        PositionImageSelectionBox();
    }

    // 이미지의 보이는 경계를 스냅한다. 칸 4변(+크롭 OFF면 페이지 끝)에 맞추며, 칸·이미지 공용 로직을 쓴다.
    private Rect SnapImageBounds(PanelImage image, ResizeEdge edge, Rect b, bool keepAspect)
    {
        if (b.Width <= 0 || b.Height <= 0)
        {
            return b;
        }

        // 소유 칸의 4변에 스냅.
        var frame = image.OwnerPanel.Frame;
        var fl = GetCanvasLeft(frame);
        var ft = GetCanvasTop(frame);
        var xs = new List<double> { fl, fl + frame.Width };
        var ys = new List<double> { ft, ft + frame.Height };

        // 크롭 OFF면 이미지가 페이지 전체에 보이므로 페이지의 완전한 끝(0·페이지 크기)에도 스냅한다.
        if (!image.IsCropped)
        {
            xs.Add(0);
            xs.Add(_pageWidth);
            ys.Add(0);
            ys.Add(_pageHeight);
        }

        return keepAspect
            ? SnapBoundsKeepAspect(edge, b, xs, ys, MinImageSize, MinImageSize)
            : SnapBoundsFree(edge, b, xs, ys, MinImageSize, MinImageSize);
    }

    // 말풍선 리사이즈를 소유 칸의 4변에 스냅한다. 입력 bounds는 칸-로컬, 스냅·가이드는 페이지 좌표로 처리 후 되돌린다.
    private Rect SnapBubbleBounds(SpeechBubble bubble, ResizeEdge edge, Rect bLocal, bool keepAspect)
    {
        if (bLocal.Width <= 0 || bLocal.Height <= 0)
        {
            return bLocal;
        }

        var origin = BubbleOverlayOrigin(bubble);
        var frame = bubble.OwnerPanel.Frame;
        var xs = new List<double> { origin.X, origin.X + frame.Width };
        var ys = new List<double> { origin.Y, origin.Y + frame.Height };

        // 크롭 OFF면 말풍선이 칸 밖으로 넘칠 수 있으므로 페이지의 끝(0·페이지 크기)에도 스냅한다.
        if (!bubble.IsCropped)
        {
            xs.Add(0);
            xs.Add(_pageWidth);
            ys.Add(0);
            ys.Add(_pageHeight);
        }

        var pageRect = new Rect(bLocal.X + origin.X, bLocal.Y + origin.Y, bLocal.Width, bLocal.Height);
        var minW = BubbleWidthSlider.Minimum;
        var minH = BubbleHeightSlider.Minimum;
        var snapped = keepAspect
            ? SnapBoundsKeepAspect(edge, pageRect, xs, ys, minW, minH)
            : SnapBoundsFree(edge, pageRect, xs, ys, minW, minH);

        return new Rect(snapped.X - origin.X, snapped.Y - origin.Y, snapped.Width, snapped.Height);
    }

    // 말풍선이 든 오버레이(칸 안)의 원점을 페이지(PageOverlay) 좌표로 환산한다(스냅 후보·가이드 좌표 변환용).
    private Point BubbleOverlayOrigin(SpeechBubble bubble)
    {
        var overlay = bubble.IsCropped ? bubble.OwnerPanel.Overlay : bubble.OwnerPanel.FreeOverlay;
        return overlay.TransformToVisual(PageOverlay).Transform(new Point(0, 0));
    }

    // Uniform으로 콘텐츠 박스(cw×ch)에 맞춰 실제 렌더되는 이미지 크기(콘텐츠 좌표, 스케일 적용 전).
    private static (double W, double H) GetImageRenderedSize(PanelImage image)
    {
        var cw = image.Content.Width;
        var ch = image.Content.Height;

        double nW = 0, nH = 0;
        if (image.Image?.Source is System.Windows.Media.Imaging.BitmapSource bmp)
        {
            nW = bmp.PixelWidth;
            nH = bmp.PixelHeight;
        }
        else if (image.Media != null && image.Media.NaturalVideoWidth > 0)
        {
            nW = image.Media.NaturalVideoWidth;
            nH = image.Media.NaturalVideoHeight;
        }

        if (nW <= 0 || nH <= 0 || cw <= 0 || ch <= 0)
        {
            return (cw, ch); // 원본 크기를 모르면 콘텐츠 박스로 대체.
        }

        var fit = Math.Min(cw / nW, ch / nH);
        return (nW * fit, nH * fit);
    }

    // 스냅 후보(세로선 xs, 가로선 ys): 페이지 끝·여백선 + 다른 칸의 모서리 및 간격만큼 떨어진 위치.
    private void CollectSnapCandidates(ComicPanel panel, out List<double> xs, out List<double> ys)
    {
        var margin = Math.Max(0, ParseDoubleOr(AutoMarginTextBox.Text, 24));
        var gutter = Math.Max(0, ParseDoubleOr(AutoGutterTextBox.Text, 14));

        // 페이지의 완전한 끝(0·페이지 크기) + 여백선.
        xs = new List<double> { 0, margin, _pageWidth - margin, _pageWidth };
        ys = new List<double> { 0, margin, _pageHeight - margin, _pageHeight };
        foreach (var p in _panels)
        {
            if (ReferenceEquals(p, panel))
            {
                continue;
            }

            var l = GetCanvasLeft(p.Frame);
            var t = GetCanvasTop(p.Frame);
            var r = l + p.Frame.Width;
            var bot = t + p.Frame.Height;
            xs.Add(l); xs.Add(r); xs.Add(l - gutter); xs.Add(r + gutter);
            ys.Add(t); ys.Add(bot); ys.Add(t - gutter); ys.Add(bot + gutter);
        }
    }

    // --- 리사이즈 스냅 공용 로직(칸·이미지 공유) ---

    // 끄는 변을 후보(xs/ys)에 맞추되 가로/세로를 독립적으로 조절한다(자유 리사이즈). 후보는 페이지 좌표.
    private Rect SnapBoundsFree(ResizeEdge edge, Rect b, List<double> xs, List<double> ys, double minW, double minH)
    {
        double x = b.X, y = b.Y, w = b.Width, h = b.Height;
        double? gx = null, gy = null;
        if (edge.HasFlag(ResizeEdge.Left) && TrySnap(x, xs, PanelSnapThreshold, out var sx)) { w += x - sx; x = sx; gx = sx; }
        if (edge.HasFlag(ResizeEdge.Right) && TrySnap(x + w, xs, PanelSnapThreshold, out var sr)) { w = sr - x; gx = sr; }
        if (edge.HasFlag(ResizeEdge.Top) && TrySnap(y, ys, PanelSnapThreshold, out var sy)) { h += y - sy; y = sy; gy = sy; }
        if (edge.HasFlag(ResizeEdge.Bottom) && TrySnap(y + h, ys, PanelSnapThreshold, out var sb)) { h = sb - y; gy = sb; }
        w = Math.Max(minW, w);
        h = Math.Max(minH, h);
        ShowSnapGuides(gx, gy);
        return new Rect(x, y, w, h);
    }

    // 끄는 변/모서리를 후보에 맞추되 '균일 배율'로만 조절해 비율을 유지한다(비율 유지 리사이즈). 후보는 페이지 좌표.
    private Rect SnapBoundsKeepAspect(ResizeEdge edge, Rect b, List<double> xs, List<double> ys, double minW, double minH)
    {
        if (b.Width <= 0 || b.Height <= 0)
        {
            return b;
        }

        // 가로/세로 앵커(고정 변).
        var anchorX = edge.HasFlag(ResizeEdge.Left) ? b.Right : b.Left;
        var anchorY = edge.HasFlag(ResizeEdge.Top) ? b.Bottom : b.Top;

        var bestDist = PanelSnapThreshold;
        var factor = 1.0;
        double? gx = null, gy = null; // 균일 배율은 단일 후보가 결정 → 그 축의 가이드만 표시.

        if (edge.HasFlag(ResizeEdge.Left) || edge.HasFlag(ResizeEdge.Right))
        {
            var draggedX = edge.HasFlag(ResizeEdge.Left) ? b.X : b.Right;
            foreach (var cx in xs)
            {
                var d = Math.Abs(cx - draggedX);
                if (d <= bestDist) { bestDist = d; factor = Math.Abs(cx - anchorX) / b.Width; gx = cx; gy = null; }
            }
        }

        if (edge.HasFlag(ResizeEdge.Top) || edge.HasFlag(ResizeEdge.Bottom))
        {
            var draggedY = edge.HasFlag(ResizeEdge.Top) ? b.Y : b.Bottom;
            foreach (var cy in ys)
            {
                var d = Math.Abs(cy - draggedY);
                if (d <= bestDist) { bestDist = d; factor = Math.Abs(cy - anchorY) / b.Height; gy = cy; gx = null; }
            }
        }

        if (factor <= 0)
        {
            factor = 1.0;
        }

        // 균일 배율이라 비율 유지. 최소 크기 보장.
        factor = Math.Max(factor, minW / b.Width);
        factor = Math.Max(factor, minH / b.Height);

        var w = b.Width * factor;
        var h = b.Height * factor;
        var nx = edge.HasFlag(ResizeEdge.Left) ? b.Right - w
            : (edge.HasFlag(ResizeEdge.Right) ? b.Left : b.X + (b.Width - w) / 2.0);
        var ny = edge.HasFlag(ResizeEdge.Top) ? b.Bottom - h
            : (edge.HasFlag(ResizeEdge.Bottom) ? b.Top : b.Y + (b.Height - h) / 2.0);
        ShowSnapGuides(gx, gy);
        return new Rect(nx, ny, w, h);
    }

    // 끄는 모서리를 스냅 후보에 맞춘다(칸 리사이즈, 자유).
    private Rect SnapPanelBounds(ComicPanel panel, ResizeEdge edge, Rect b)
    {
        CollectSnapCandidates(panel, out var xs, out var ys);
        return SnapBoundsFree(edge, b, xs, ys, PanelWidthSlider.Minimum, PanelHeightSlider.Minimum);
    }

    // 비율 유지(Shift) 리사이즈용 스냅: 균일 배율로만 조정해 비율을 깨지 않는다.
    private Rect SnapPanelBoundsKeepAspect(ComicPanel panel, ResizeEdge edge, Rect b)
    {
        CollectSnapCandidates(panel, out var xs, out var ys);
        return SnapBoundsKeepAspect(edge, b, xs, ys, PanelWidthSlider.Minimum, PanelHeightSlider.Minimum);
    }

    // 칸 이동 시 좌/우(또는 상/하) 변 중 가까운 후보에 맞춰 위치를 스냅한다.
    private (double X, double Y) SnapPanelPosition(ComicPanel panel, double x, double y)
    {
        CollectSnapCandidates(panel, out var xs, out var ys);
        var w = panel.Frame.Width;
        var h = panel.Frame.Height;

        double? gx = null, gy = null;
        if (TrySnapEdgePair(x, x + w, xs, PanelSnapThreshold, out var nx, out var cx)) { x = nx; gx = cx; }
        if (TrySnapEdgePair(y, y + h, ys, PanelSnapThreshold, out var ny, out var cy)) { y = ny; gy = cy; }
        ShowSnapGuides(gx, gy);
        return (x, y);
    }

    // 말풍선 이동 시 소유 칸의 변·중앙에 스냅한다. 입력/반환은 칸-로컬, 스냅·가이드는 페이지 좌표로 처리.
    private (double X, double Y) SnapBubblePosition(SpeechBubble bubble, double x, double y)
    {
        var origin = BubbleOverlayOrigin(bubble);
        var frame = bubble.OwnerPanel.Frame;
        double pw = frame.Width, ph = frame.Height;
        double w = bubble.Container.Width, h = bubble.Container.Height;
        var xs = new List<double> { origin.X, origin.X + pw };
        var ys = new List<double> { origin.Y, origin.Y + ph };

        // 크롭 OFF면 페이지 끝(0·페이지 크기)에도 스냅한다.
        if (!bubble.IsCropped)
        {
            xs.Add(0);
            xs.Add(_pageWidth);
            ys.Add(0);
            ys.Add(_pageHeight);
        }

        var px = x + origin.X;
        var py = y + origin.Y;
        double? gx = null, gy = null;

        if (TrySnapEdgePair(px, px + w, xs, PanelSnapThreshold, out var nx, out var cx)) { px = nx; gx = cx; }
        else if (Math.Abs(px + w / 2 - (origin.X + pw / 2)) <= PanelSnapThreshold) { px = origin.X + pw / 2 - w / 2; gx = origin.X + pw / 2; } // 가로 중앙

        if (TrySnapEdgePair(py, py + h, ys, PanelSnapThreshold, out var ny, out var cy)) { py = ny; gy = cy; }
        else if (Math.Abs(py + h / 2 - (origin.Y + ph / 2)) <= PanelSnapThreshold) { py = origin.Y + ph / 2 - h / 2; gy = origin.Y + ph / 2; } // 세로 중앙

        ShowSnapGuides(gx, gy);
        return (px - origin.X, py - origin.Y);
    }

    // start(좌/상)·end(우/하) 두 변 중 후보에 가장 가까운 쪽으로 스냅한 새 start 좌표.
    private static bool TrySnapEdgePair(double start, double end, List<double> candidates, double threshold, out double newStart, out double guide)
    {
        newStart = start;
        guide = 0;
        var best = threshold;
        var found = false;
        foreach (var c in candidates)
        {
            var d1 = Math.Abs(c - start);
            if (d1 <= best) { best = d1; newStart = c; guide = c; found = true; }

            var d2 = Math.Abs(c - end);
            if (d2 <= best) { best = d2; newStart = c - (end - start); guide = c; found = true; }
        }

        return found;
    }

    // --- 스냅 가이드 선(이동 시 스냅된 위치 표시) ---

    private void EnsureSnapGuides()
    {
        if (_snapGuideX == null)
        {
            _snapGuideX = CreateSnapGuideLine();
            _snapGuideY = CreateSnapGuideLine();
        }

        // 페이지 재구성(실행취소·페이지 전환)이 PageOverlay를 비우므로, 빠졌으면 다시 넣는다.
        foreach (var line in new[] { _snapGuideX, _snapGuideY! })
        {
            if (!PageOverlay.Children.Contains(line))
            {
                PageOverlay.Children.Add(line);
                Panel.SetZIndex(line, int.MaxValue - 4);
            }
        }
    }

    private static System.Windows.Shapes.Line CreateSnapGuideLine() => new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x5F)),
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        IsHitTestVisible = false,
        Visibility = Visibility.Hidden
    };

    // 페이지 좌표 기준. x가 있으면 세로선, y가 있으면 가로선을 페이지 전체에 그린다(없으면 숨김).
    private void ShowSnapGuides(double? x, double? y)
    {
        EnsureSnapGuides();

        if (x is double gx)
        {
            _snapGuideX!.X1 = gx; _snapGuideX.X2 = gx; _snapGuideX.Y1 = 0; _snapGuideX.Y2 = _pageHeight;
            _snapGuideX.Visibility = Visibility.Visible;
        }
        else
        {
            _snapGuideX!.Visibility = Visibility.Hidden;
        }

        if (y is double gy)
        {
            _snapGuideY!.X1 = 0; _snapGuideY.X2 = _pageWidth; _snapGuideY.Y1 = gy; _snapGuideY.Y2 = gy;
            _snapGuideY.Visibility = Visibility.Visible;
        }
        else
        {
            _snapGuideY!.Visibility = Visibility.Hidden;
        }
    }

    private void ClearSnapGuides()
    {
        if (_snapGuideX != null) _snapGuideX.Visibility = Visibility.Hidden;
        if (_snapGuideY != null) _snapGuideY.Visibility = Visibility.Hidden;
    }

    private static bool TrySnap(double value, List<double> candidates, double threshold, out double snapped)
    {
        snapped = value;
        var best = threshold;
        var found = false;
        foreach (var c in candidates)
        {
            var d = Math.Abs(c - value);
            if (d <= best)
            {
                best = d;
                snapped = c;
                found = true;
            }
        }

        return found;
    }
}
