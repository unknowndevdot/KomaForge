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
            SetHoverHighlight(null);
            return;
        }

        // 1) 작은 UI 핸들(꼬리·텍스트·모서리·크기 변경) 위에 있으면 그 핸들을 알려준다.
        var label = GetHoverHandleLabel(source);

        if (label == null)
        {
            // 2) 그 외에는 클릭하면 실제로 선택될 대상을 예측해, 그 대상을 미리 강조한다.
            var target = PredictNormalClickTarget(source);
            SetHoverHighlight(target);
            label = target switch
            {
                ComicPanel => "칸",
                PanelImage => "이미지",
                SpeechBubble => "말풍선",
                _ => null
            };
        }
        else
        {
            // 핸들 위에서는 오브젝트 호버 강조를 끈다(핸들 조작이 우선).
            SetHoverHighlight(null);
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

    // 일반(비-Ctrl) 클릭이 실제로 선택할 오브젝트를 예측한다(없으면 null).
    // 실제 클릭(HandleSelectionPress)과 동일하게, 이미 선택된 겹친 오브젝트 위에서는 '한 단계 안쪽'을 가리킨다.
    private object? PredictNormalClickTarget(DependencyObject? source)
    {
        var (top, panel, framePoint) = ResolveTopTarget(source);
        if (panel == null)
        {
            return top; // 칸 밖(자유 말풍선 등)은 순환 대상이 없다.
        }

        // 클릭과 동일한 스택+순환 규칙을 적용한다.
        var stack = CollectSelectablesAt(panel, framePoint);
        if (top != null && stack.Contains(top))
        {
            stack.Remove(top);
            stack.Insert(0, top);
        }

        if (stack.Count == 0)
        {
            return null;
        }

        var idx = stack.FindIndex(IsSelectedObjectAny);
        return idx < 0 ? stack[0] : stack[(idx + 1) % stack.Count];
    }

    // 클릭이 실제로 라우팅될 '최상단' 대상과 그 칸·칸로컬 좌표를 구한다(고정 오브젝트는 히트테스트 통과).
    private (object? Top, ComicPanel? Panel, Point FramePoint) ResolveTopTarget(DependencyObject? source)
    {
        var node = source;
        Border? bubbleBorder = null;
        ComicPanel? framePanel = null;
        while (node != null)
        {
            if (node is Border bb && Equals(bb.Tag, "SpeechBubble") && bubbleBorder == null)
            {
                bubbleBorder = bb;
            }

            if (node is Border frameBorder && frameBorder.Tag is int)
            {
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

        var bubble = bubbleBorder != null ? FindBubbleByContainer(bubbleBorder) : null;

        if (framePanel == null)
        {
            return (bubble, null, default);
        }

        var local = Mouse.GetPosition(framePanel.Frame);

        // 칸이 잠기면 칸 자신은 물론 내부 말풍선·이미지도 선택 대상이 아니다.
        if (framePanel.IsLocked)
        {
            return (null, framePanel, local);
        }

        // BeginPanelDrag와 동일한 우선순위: 테두리(바깥 밴드 포함)면 위에 말풍선/이미지가 있어도 칸.
        if (IsOnPanelBorder(framePanel, local))
        {
            return (framePanel, framePanel, local);
        }

        // 테두리가 아니면 말풍선 > 이미지 > 칸 순.
        if (bubble != null)
        {
            return (bubble, framePanel, local);
        }

        var image = FindImageAtPoint(framePanel, local, includeLocked: false);
        if (image != null)
        {
            return (image, framePanel, local);
        }

        return (framePanel, framePanel, local);
    }

    // 말풍선 컨테이너 Border로부터 SpeechBubble 모델을 찾는다.
    private SpeechBubble? FindBubbleByContainer(Border container)
    {
        foreach (var p in _panels)
        {
            foreach (var b in p.Bubbles)
            {
                if (ReferenceEquals(b.Container, container))
                {
                    return b;
                }
            }
        }

        return null;
    }

    // 호버 강조색(앰버) — 현재 선택색(틸)과 구분해 '미리보기'임을 알린다.
    private static readonly Brush HoverAccentBrush = CreateFrozenBrush(224, 142, 52);
    private static readonly Brush HoverAccentTint = CreateFrozenBrush(224, 142, 52, 55);

    // 커서 아래의 '클릭하면 선택될' 오브젝트를 강조한다(칸·말풍선=테두리, 이미지=전체 틴트).
    private void SetHoverHighlight(object? target)
    {
        // '선택 미리보기 강조'가 꺼져 있으면 강조하지 않는다(툴팁 라벨은 별개로 동작).
        if (!_selectionPreviewEnabled)
        {
            target = null;
        }

        if (ReferenceEquals(target, _hoveredObject))
        {
            return;
        }

        ClearHoverHighlight();
        _hoveredObject = target;

        switch (target)
        {
            case ComicPanel p:
                // 이미 선택된 칸이면 선택색이 우선(덮어쓰지 않음).
                if (!(_selectionKind == SelectionKind.Panel && p == _selectedPanel))
                {
                    foreach (var line in p.QuadBorderLines)
                    {
                        line.Stroke = HoverAccentBrush;
                    }
                }
                break;

            case SpeechBubble b:
                var origin = GetBubblePageOrigin(b);
                ShowHoverBox(new Rect(origin.X, origin.Y, b.Container.Width, b.Container.Height));
                break;

            case PanelImage img:
                if (!(_selectionKind == SelectionKind.Image && img == _selectedImage))
                {
                    img.SelectionBorder.Background = HoverAccentTint;
                    img.SelectionBorder.Visibility = Visibility.Visible;
                }
                // 틴트와 함께 UI 박스(테두리)도 보여 준다(말풍선·칸과 동일한 느낌).
                ShowHoverBox(GetImageVisiblePageBounds(img));
                break;
        }
    }

    // 현재 호버 강조를 원래대로 되돌린다(선택 중인 오브젝트는 선택색을 유지).
    private void ClearHoverHighlight()
    {
        switch (_hoveredObject)
        {
            case ComicPanel p:
                if (!(_selectionKind == SelectionKind.Panel && p == _selectedPanel))
                {
                    // 호버 해제 시 칸의 실제 테두리색으로 복원한다(검정으로 고정하면 커스텀 색이 지워짐).
                    var borderBrush = new SolidColorBrush(p.BorderColor);
                    foreach (var line in p.QuadBorderLines)
                    {
                        line.Stroke = borderBrush;
                    }
                }
                break;

            case SpeechBubble:
                HideHoverBox();
                break;

            case PanelImage img:
                if (!(_selectionKind == SelectionKind.Image && img == _selectedImage))
                {
                    img.SelectionBorder.Visibility = Visibility.Hidden;
                }
                HideHoverBox();
                break;
        }

        _hoveredObject = null;
    }

    // 호버 강조 박스를 페이지 좌표 사각형에 맞춰 표시한다(말풍선·이미지 공용).
    private void ShowHoverBox(Rect bounds)
    {
        if (_hoverBox == null)
        {
            _hoverBox = new Border
            {
                BorderBrush = HoverAccentBrush,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };
        }

        // 페이지 재구성(실행취소·페이지 전환)이 PageOverlay를 비우므로, 빠졌으면 다시 넣는다.
        if (!PageOverlay.Children.Contains(_hoverBox))
        {
            PageOverlay.Children.Add(_hoverBox);
            Panel.SetZIndex(_hoverBox, int.MaxValue - 3);
        }

        Canvas.SetLeft(_hoverBox, bounds.X);
        Canvas.SetTop(_hoverBox, bounds.Y);
        _hoverBox.Width = Math.Max(0, bounds.Width);
        _hoverBox.Height = Math.Max(0, bounds.Height);
        _hoverBox.Visibility = Visibility.Visible;
    }

    private void HideHoverBox()
    {
        if (_hoverBox != null)
        {
            _hoverBox.Visibility = Visibility.Hidden;
        }
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

        if (_bubbleResizeHandles != null && System.Array.IndexOf(_bubbleResizeHandles, thumb) >= 0)
        {
            return "말풍선 크기 변경";
        }

        if (_panelResizeHandles != null && System.Array.IndexOf(_panelResizeHandles, thumb) >= 0)
        {
            return "칸 크기 변경";
        }

        if (_imageResizeHandles != null && System.Array.IndexOf(_imageResizeHandles, thumb) >= 0)
        {
            return "이미지 크기 변경";
        }

        return null;
    }

    private void PageSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverPopup != null)
        {
            _hoverPopup.IsOpen = false;
        }

        SetHoverHighlight(null);
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


    // 선택 판별: 현재 선택된 오브젝트인지.
    private bool IsSelectedObject(ComicPanel panel) => _selectionKind == SelectionKind.Panel && ReferenceEquals(_selectedPanel, panel);
    private bool IsSelectedObject(PanelImage image) => _selectionKind == SelectionKind.Image && ReferenceEquals(_selectedImage, image);
    private bool IsSelectedObject(SpeechBubble bubble) => _selectionKind == SelectionKind.Bubble && ReferenceEquals(_selectedBubble, bubble);

    // 현재 캡처 중인 요소(말풍선 컨테이너·칸 프레임 등)가 있으면 해제한다.
    // 칸 프레임의 업 핸들러가 e.Handled로 자식 컨테이너의 업 핸들러를 가려 캡처가 안 풀리면,
    // 이후 클릭이 모두 그 오브젝트로 가서 선택 범위가 화면 전체처럼 되는 문제를 막는다.
    private static void ReleaseMouseCaptureIfAny()
    {
        if (Mouse.Captured is UIElement captured)
        {
            captured.ReleaseMouseCapture();
        }
    }

    // 마우스 업 시점에 (드래그 없이) 보류된 대상을 선택한다.
    private void CommitPendingSelect()
    {
        var target = _pendingSelect;
        _pendingSelect = null;
        ReleaseMouseCaptureIfAny();

        switch (target)
        {
            case ComicPanel p: SelectPanel(p); ScrollInspectorToSection(); break;
            case PanelImage img: SelectImage(img); ScrollInspectorToSection(); break;
            case SpeechBubble b: SelectBubble(b); ScrollInspectorToSection(); break;
        }
    }

    private void BeginPanelDrag(ComicPanel panel, MouseButtonEventArgs e)
    {
        if (!IsInspectorOpen())
        {
            return; // 뷰어 모드: 칸/이미지 선택·이동 안 함.
        }

        var point = e.GetPosition(panel.Frame);
        var onBorder = IsOnPanelBorder(panel, point);

        // 실제로 눌린(최상단) 대상을 구한다. 테두리면 칸, 아니면 핸들/말풍선엔 양보하고 이미지>칸.
        object? knownTop;
        if (onBorder)
        {
            knownTop = panel;
        }
        else
        {
            if (IsInsideResizeHandle(e.OriginalSource as DependencyObject) || IsInsideBubble(e.OriginalSource as DependencyObject))
            {
                return;
            }

            knownTop = (object?)FindImageAtPoint(panel, point) ?? panel;
        }

        HandleSelectionPress(panel, point, e, panel.Frame, knownTop);
    }

    // 겹친 오브젝트 순환 선택 + 두 단계 드래그 진입을 공통 처리한다.
    // knownTop = 이 핸들러가 실제로 누른(최상단) 오브젝트. captureElement = 마우스 캡처 대상.
    private void HandleSelectionPress(ComicPanel panel, Point framePoint, MouseButtonEventArgs e, IInputElement captureElement, object? knownTop)
    {
        var stack = CollectSelectablesAt(panel, framePoint);

        // 실제로 클릭을 받은 오브젝트를 스택 최상단으로 보장(핸들러-스택 z순서 일치).
        if (knownTop != null && stack.Contains(knownTop))
        {
            stack.Remove(knownTop);
            stack.Insert(0, knownTop);
        }

        if (stack.Count == 0)
        {
            return; // 선택 가능한 대상 없음(잠긴 칸 빈 영역 등).
        }

        _pendingSelect = null;
        _pendingCycle = null;
        _pendingDownPos = e.GetPosition(PageOverlay);

        var idx = stack.FindIndex(IsSelectedObjectAny);
        if (idx < 0)
        {
            // 이 지점에 현재 선택이 없음 → 업에서 최상단 선택(드래그는 선택된 것만).
            _pendingSelect = stack[0];
        }
        else
        {
            // 현재 선택을 다시 누름 → 클릭(이동 없음)이면 한 단계 안쪽으로 순환(마지막 다음은 첫번째).
            _pendingCycle = stack[(idx + 1) % stack.Count];

            // 선택된 오브젝트가 (앞 오브젝트에 가려져 있어도) 커서 아래에 있으면 드래그를 허용한다.
            // 클릭(이동 없음)이면 순환, 데드존을 넘겨 끌면 선택된 오브젝트가 이동한다.
            BeginDragOfSelected(panel, framePoint, e);
        }

        captureElement.CaptureMouse();
        e.Handled = true;
    }

    private bool IsSelectedObjectAny(object o) => o switch
    {
        ComicPanel p => IsSelectedObject(p),
        PanelImage img => IsSelectedObject(img),
        SpeechBubble b => IsSelectedObject(b),
        _ => false
    };

    // 현재 선택된 오브젝트의 드래그를 시작한다(종류별 상태/시작점 설정).
    private void BeginDragOfSelected(ComicPanel panel, Point framePoint, MouseButtonEventArgs e)
    {
        switch (_selectionKind)
        {
            case SelectionKind.Panel when _selectedPanel != null:
                _isDraggingPanel = true;
                _dragStart = framePoint;
                break;
            case SelectionKind.Image when _selectedImage != null:
                _isDraggingPanelImage = true;
                _imageDragStart = framePoint;
                _imageDragOrigin = new Point(_selectedImage.Translate.X, _selectedImage.Translate.Y);
                panel.Frame.Cursor = Cursors.Hand;
                break;
            case SelectionKind.Bubble when _selectedBubble != null:
                _isDraggingBubble = true;
                _dragStart = e.GetPosition(_selectedBubble.Container);
                break;
        }
    }

    // 선택된 이미지가 칸 밖으로 벗어난 경우, 칸 프레임 밖(=BeginPanelDrag가 닿지 않는 곳)에서 이미지 위를
    // 눌렀을 때 호출해 이미지 드래그를 시작한다. 소유 칸 프레임에 마우스를 캡처해 기존 이동/끝 핸들러를 재사용한다.
    private void BeginOverflowImageDrag(MouseButtonEventArgs e)
    {
        var image = _selectedImage;
        if (image == null)
        {
            return;
        }
        var frame = image.OwnerPanel.Frame;
        _pendingSelect = null;
        _pendingCycle = null;
        _pendingDownPos = e.GetPosition(PageOverlay);
        _isDraggingPanelImage = true;
        _imageDragStart = e.GetPosition(frame); // 이동 계산은 소유 칸 프레임 좌표 기준(프레임 밖 음수 좌표도 정상).
        _imageDragOrigin = new Point(image.Translate.X, image.Translate.Y);
        frame.Cursor = Cursors.Hand;
        frame.CaptureMouse(); // 캡처 후엔 프레임의 PreviewMouseMove/Up이 칸 밖에서도 모두 받아 드래그가 이어진다.
    }

    // 선택된 이미지가 (칸 밖으로 벗어난 부분 포함) 커서 아래에 있고, 소유 칸 프레임 밖을 눌렀는지.
    // 프레임 안 클릭은 BeginPanelDrag가 정상 처리하므로 그 경우는 false(가로채지 않음).
    private bool ShouldBeginOverflowImageDrag(MouseButtonEventArgs e)
    {
        if (!IsInspectorOpen() || _selectionKind != SelectionKind.Image || _selectedImage == null)
        {
            return false;
        }
        var frame = _selectedImage.OwnerPanel.Frame;
        return !IsMouseOverElement(frame, e) && IsMouseOverElement(_selectedImage.Content, e);
    }

    // 클릭(이동 없음)으로 끝났으면 보류된 '한 단계 안쪽' 대상을 선택한다(드래그였으면 순환하지 않음).
    private void CommitPendingCycleIfClick(MouseButtonEventArgs e)
    {
        var target = _pendingCycle;
        _pendingCycle = null;
        if (target == null)
        {
            return;
        }

        if ((e.GetPosition(PageOverlay) - _pendingDownPos).Length > PendingMoveCancelThreshold)
        {
            return; // 이동이 있었으면 드래그로 보고 순환하지 않는다.
        }

        ReleaseMouseCaptureIfAny();
        switch (target)
        {
            case ComicPanel p: SelectPanel(p); ScrollInspectorToSection(); break;
            case PanelImage img: SelectImage(img); ScrollInspectorToSection(); break;
            case SpeechBubble b: SelectBubble(b); ScrollInspectorToSection(); break;
        }
    }

    // 클릭 지점에 겹쳐 있는 선택 가능한 오브젝트를 위→아래 순서로 모은다(잠긴 것 제외).
    private List<object> CollectSelectablesAt(ComicPanel panel, Point framePoint)
    {
        var stack = new List<object>();

        // 칸이 잠기면 칸 자신은 물론 내부 이미지·말풍선도 선택 불가.
        if (panel.IsLocked)
        {
            return stack;
        }

        var onBorder = IsOnPanelBorder(panel, framePoint);

        // 테두리 클릭이면 칸을 최상단으로(테두리 위에 말풍선/이미지가 있어도 칸 우선).
        if (onBorder && !panel.IsLocked)
        {
            stack.Add(panel);
        }

        // 말풍선(위에 그려진 것부터). 컨테이너 사각형 전체가 클릭 가능(투명 배경).
        for (var i = panel.Bubbles.Count - 1; i >= 0; i--)
        {
            var bubble = panel.Bubbles[i];
            if (!bubble.IsLocked && BubbleContainsFramePoint(bubble, framePoint))
            {
                stack.Add(bubble);
            }
        }

        // 이미지(크롭 OFF가 위, 그룹 내 높은 인덱스가 위), 불투명 픽셀만.
        AddImagesAtPoint(panel, framePoint, cropped: false, stack);
        AddImagesAtPoint(panel, framePoint, cropped: true, stack);

        // 칸 몸체(테두리 아님)는 맨 아래.
        if (!panel.IsLocked && !stack.Contains(panel))
        {
            stack.Add(panel);
        }

        return stack;
    }

    private static void AddImagesAtPoint(ComicPanel panel, Point framePoint, bool cropped, List<object> stack)
    {
        for (var i = panel.Images.Count - 1; i >= 0; i--)
        {
            var image = panel.Images[i];
            if (image.IsCropped == cropped && !image.IsLocked && IsOpaqueImagePixelAtPoint(image, framePoint))
            {
                stack.Add(image);
            }
        }
    }

    private static bool BubbleContainsFramePoint(SpeechBubble bubble, Point framePoint)
    {
        var l = GetCanvasLeft(bubble.Container);
        var t = GetCanvasTop(bubble.Container);
        return framePoint.X >= l && framePoint.Y >= t
            && framePoint.X <= l + bubble.Container.Width
            && framePoint.Y <= t + bubble.Container.Height;
    }

    private void DragPanel(ComicPanel panel, MouseEventArgs e)
    {
        if (HandlePendingCancel(e))
        {
            return;
        }

        // 칸 프레임이 캡처했어도(혹은 가려진 다른 종류가 선택돼 있어도) 선택된 오브젝트를 드래그한다.
        DragSelectedObject(e);
    }

    // 보류 선택(미선택 오브젝트 다운) 중 임계 이상 이동하면 클릭이 아니라고 보고 선택을 취소한다.
    // 보류 선택을 처리 중이면 true(이 동안에는 드래그하지 않음).
    private bool HandlePendingCancel(MouseEventArgs e)
    {
        if (_pendingSelect == null)
        {
            return false;
        }

        if ((e.GetPosition(PageOverlay) - _pendingDownPos).Length > PendingMoveCancelThreshold)
        {
            _pendingSelect = null;
            ReleaseMouseCaptureIfAny();
        }

        return true;
    }

    // 어떤 요소가 마우스를 캡처했든, 현재 선택된 오브젝트를 종류에 맞게 드래그한다(가려져 있어도).
    private void DragSelectedObject(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 캡처 밖에서 버튼이 떼진 경우의 안전 정리(정상 업은 EndPanelDrag/EndBubbleDrag가 처리).
            if (_isDraggingPanel || _isDraggingPanelImage || _isDraggingBubble)
            {
                _isDraggingPanel = _isDraggingPanelImage = _isDraggingBubble = false;
                ReleaseMouseCaptureIfAny();
            }

            return;
        }

        if (!_isDraggingPanel && !_isDraggingPanelImage && !_isDraggingBubble)
        {
            return;
        }

        // 데드존: 임계 이내면 아직 클릭(순환)일 수 있으므로 이동을 적용하지 않는다.
        if (!DragDeadzonePassed(e))
        {
            return;
        }

        if (_isDraggingPanelImage)
        {
            DragSelectedImage(e);
        }
        else if (_isDraggingBubble)
        {
            DragSelectedBubble(e);
        }
        else if (_isDraggingPanel)
        {
            DragSelectedPanelBody(e);
        }

        e.Handled = true;
    }

    // 선택을 누른 뒤 임계 이상 움직였으면 드래그로 확정하고(순환 취소) true. 그 전까지는 false.
    private bool DragDeadzonePassed(MouseEventArgs e)
    {
        if (_pendingCycle == null)
        {
            return true; // 이미 드래그 확정.
        }

        if ((e.GetPosition(PageOverlay) - _pendingDownPos).Length <= PendingMoveCancelThreshold)
        {
            return false; // 아직 데드존 안.
        }

        _pendingCycle = null; // 임계 초과 → 드래그 확정, 순환 취소.
        return true;
    }

    private void DragSelectedImage(MouseEventArgs e)
    {
        var image = _selectedImage;
        if (image == null)
        {
            return;
        }

        // 절대 위치(시작 Translate + 마우스 이동량)로 계산해 스냅 보정이 누적되지 않게 한다.
        var imagePosition = e.GetPosition(image.OwnerPanel.Frame);
        var tx = _imageDragOrigin.X + (imagePosition.X - _imageDragStart.X);
        var ty = _imageDragOrigin.Y + (imagePosition.Y - _imageDragStart.Y);

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            (tx, ty) = SnapImageTranslate(image, tx, ty);
        }
        else
        {
            ClearSnapGuides();
        }

        image.Translate.X = tx;
        image.Translate.Y = ty;
        PositionImageSelectionBox();
    }

    private void DragSelectedPanelBody(MouseEventArgs e)
    {
        var panel = _selectedPanel;
        if (panel == null)
        {
            return;
        }

        var position = e.GetPosition(PanelCanvas);
        var x = position.X - _dragStart.X;
        var y = position.Y - _dragStart.Y;

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            (x, y) = SnapPanelPosition(panel, x, y);
        }
        else
        {
            ClearSnapGuides();
        }

        x = ClampPanelX(x, panel.Frame.Width);
        y = ClampPanelY(y, panel.Frame.Height);
        SetPanelPosition(panel, x, y);
        LoadPanelValues(panel);
        UpdateFreeBubblesForPanel(panel);
        PositionPanelCornerHandles();
    }

    private void DragSelectedBubble(MouseEventArgs e)
    {
        var bubble = _selectedBubble;
        if (bubble == null)
        {
            return;
        }

        var canvas = (Canvas)bubble.Container.Parent;
        var position = e.GetPosition(canvas);
        var x = position.X - _dragStart.X;
        var y = position.Y - _dragStart.Y;

        // 소유 칸의 변·중앙에 스냅(Alt로 일시 해제).
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            (x, y) = SnapBubblePosition(bubble, x, y);
        }
        else
        {
            ClearSnapGuides();
        }

        Canvas.SetLeft(bubble.Container, x);
        Canvas.SetTop(bubble.Container, y);

        var relative = GetBubblePositionInOwnerPanel(bubble);
        _isLoadingInspector = true;
        BubbleXSlider.Value = Math.Clamp(relative.X, BubbleXSlider.Minimum, BubbleXSlider.Maximum);
        BubbleYSlider.Value = Math.Clamp(relative.Y, BubbleYSlider.Minimum, BubbleYSlider.Maximum);
        _isLoadingInspector = false;

        UpdateMergedBubbleOutlines(bubble.OwnerPanel);
        PositionSelectedTailHandles();
        UpdateInspectorLabels();
    }

    private void EndPanelDrag(ComicPanel panel, MouseButtonEventArgs e)
    {
        // 드래그가 아니었던 클릭이면 업 시점에 선택(캡처 해제로 _pendingSelect가 지워지기 전에 먼저 처리).
        if (_pendingSelect != null)
        {
            CommitPendingSelect();
        }
        else
        {
            // 이미 선택된 겹친 오브젝트를 다시 클릭한 경우: 한 단계 안쪽으로 순환 선택.
            CommitPendingCycleIfClick(e);
        }

        EndPanelDrag(panel);

        // 칸 프레임은 모든 오브젝트(말풍선 포함)의 조상이라, 마우스 업이 이 핸들러로 먼저 와서
        // e.Handled로 자식(말풍선 컨테이너)의 업 핸들러를 가린다. 그래서 어떤 요소가 잡았든
        // 여기서 캡처를 확실히 해제해야 캡처가 남는 문제(선택 범위=화면 전체)가 안 생긴다.
        ReleaseMouseCaptureIfAny();
        e.Handled = true;
        RefreshHoverAfterSelect();
    }

    // 클릭으로 선택이 바뀐 직후, 마우스를 움직이지 않아도 호버 강조가 '다음 순환 대상'으로 갱신되게 한다.
    // 입력 이벤트와 캡처 변경이 모두 반영되어 hit-test가 안정된 뒤 실행되도록 Input 우선순위로 미룬다.
    private void RefreshHoverAfterSelect()
    {
        Dispatcher.BeginInvoke(
            new Action(() => UpdateHoverTooltip(Mouse.DirectlyOver as DependencyObject)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EndPanelDrag(ComicPanel panel)
    {
        // 칸 프레임 업 핸들러는 모든 업(말풍선 포함)에서 먼저 실행되므로 모든 드래그 플래그를 정리한다.
        _isDraggingPanel = false;
        _isDraggingPanelImage = false;
        _isDraggingBubble = false;
        ClearSnapGuides(); // 드래그 끝나면 스냅 가이드 선 숨김.
        panel.Frame.Cursor = Cursors.SizeAll;

        if (panel.Frame.IsMouseCaptured)
        {
            panel.Frame.ReleaseMouseCapture();
        }
    }

    // 휠로 (이미 선택된) 이미지 크기를 조절한다.
    private void ZoomImage(PanelImage image, MouseWheelEventArgs e)
    {
        var step = e.Delta > 0 ? 1.08 : 0.92;
        // 상한은 원본 픽셀의 4배(큰 이미지는 원본 100%가 이미 높아 고정 5.0이면 더 못 키우므로).
        var max = MaxImageZoomScale(image);
        // 가로/세로 배율에 각각 같은 비율을 곱해(자유 리사이즈로 달라진 종횡비는 유지) 중심 고정 확대/축소.
        image.Scale.ScaleX = Math.Clamp(image.Scale.ScaleX * step, 0.3, max);
        image.Scale.ScaleY = Math.Clamp(image.Scale.ScaleY * step, 0.3, max);
        PositionImageSelectionBox();
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
                var img = lastImage;
                SelectImage(img);
                ScrollInspectorToSection();
                // 갓 추가된 이미지는 레이아웃 전이라 선택 박스·핸들이 어긋날 수 있다.
                // 레이아웃이 끝난 뒤 선택을 다시 확정해 제대로 선택된 상태로 만든다.
                Dispatcher.BeginInvoke(
                    new Action(() => { if (_panels.Contains(img.OwnerPanel) && img.OwnerPanel.Images.Contains(img)) SelectImage(img); }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }

            UpdateStatus($"{panel.Number}번 칸에 이미지 {paths.Count}개를 드롭했습니다.");
            e.Handled = true;
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

        if (IsInsideResizeHandle(e.OriginalSource as DependencyObject))
        {
            return; // 핸들은 별도 처리.
        }

        var panel = bubble.OwnerPanel;
        var framePoint = e.GetPosition(panel.Frame);
        HandleSelectionPress(panel, framePoint, e, bubble.Container, bubble);
    }

    private void DragBubble(SpeechBubble bubble, MouseEventArgs e)
    {
        if (HandlePendingCancel(e))
        {
            return;
        }

        // 말풍선 컨테이너가 캡처했어도, 가려진 채 선택된 다른 오브젝트까지 포함해 선택 대상을 드래그한다.
        DragSelectedObject(e);
    }

    private void EndBubbleDrag(SpeechBubble bubble, MouseButtonEventArgs e)
    {
        // 드래그가 아니었던 클릭이면 업 시점에 선택(캡처 해제 전에 먼저).
        if (_pendingSelect != null)
        {
            CommitPendingSelect();
        }
        else
        {
            CommitPendingCycleIfClick(e);
        }

        EndBubbleDrag(bubble);
        ReleaseMouseCaptureIfAny();
        e.Handled = true;
        RefreshHoverAfterSelect();
    }

    private void EndBubbleDrag(SpeechBubble bubble)
    {
        _isDraggingBubble = false;
        ClearSnapGuides(); // 드래그 끝나면 스냅 가이드 선 숨김.

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

    // (이미 선택된) 말풍선을 마우스 휠로 확대/축소(중앙 고정).
    private void ZoomBubble(SpeechBubble bubble, MouseWheelEventArgs e)
    {
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
