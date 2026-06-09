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

        var dW = width - panel.Frame.Width;
        var dH = height - panel.Frame.Height;
        panel.Frame.Width = width;
        panel.Frame.Height = height;
        ApplyPivotShift(panel, dW, dH);
        UpdatePanelImageSizes(panel);
        LoadPanelValues(panel);
        UpdateFreeBubblesForPanel(panel);
        PositionPanelCornerHandles();
    }

    // 칸 크기 변화량(dW,dH)에 맞춰 각 이미지·말풍선을 자기 기준점(Pivot)만큼 이동시킨다.
    // Pivot X:0=좌,1=우 / Y:0=하,1=상. (0,1)=좌상단이면 이동량이 0이라 그대로 고정된다.
    private void ApplyPivotShift(ComicPanel panel, double dW, double dH)
    {
        if (dW == 0 && dH == 0)
        {
            return;
        }

        foreach (var image in panel.Images)
        {
            image.Translate.X += image.PivotX * dW;
            image.Translate.Y += (1 - image.PivotY) * dH;
        }

        foreach (var bubble in panel.Bubbles)
        {
            var pos = GetBubblePositionInOwnerPanel(bubble);
            SetBubblePositionInOwnerPanel(bubble, pos.X + bubble.PivotX * dW, pos.Y + (1 - bubble.PivotY) * dH);
        }
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

}
