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
        panel.Frame.Visibility = Visibility.Visible; // 화면 밖에서 컬링됐어도 선택 시 보이게.
        _selectedImage = null;
        _selectedBubble = null;
        _selectedBubbleTail = null;
        SetPanelListSelection(panel);
        _isLoadingInspector = true;
        PanelLockCheckBox.IsChecked = panel.IsLocked;
        PanelCornerModeCheckBox.IsChecked = panel.CornerMode;
        SelectBubbleColorInCombo(PanelBackgroundColorComboBox, panel.QuadFill.Fill);
        SelectBubbleColorInCombo(PanelBorderColorComboBox, new SolidColorBrush(panel.BorderColor));
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
        BubbleThoughtTailCheckBox.IsChecked = tail.ThoughtTail; // '생각 꼬리'도 꼬리 개별 값.
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
        ImagePivotXBox.Text = $"{image.PivotX:0.##}";
        ImagePivotYBox.Text = $"{image.PivotY:0.##}";
        ImageGradientComboBox.SelectedIndex = image.GradientDirection switch
        {
            ImageGradientDirection.Top => 1,
            ImageGradientDirection.Bottom => 2,
            ImageGradientDirection.Left => 3,
            ImageGradientDirection.Right => 4,
            _ => 0
        };
        SelectBubbleColorInCombo(ImageGradientColorComboBox, new SolidColorBrush(image.GradientColor));
        ImageGradientStartSlider.Value = Math.Clamp(image.GradientStart, 0, 100);
        ImageGradientEndSlider.Value = Math.Clamp(image.GradientEnd, 0, 100);
        ImageGradientStartText.Text = $"시작: {ImageGradientStartSlider.Value:0}%";
        ImageGradientEndText.Text = $"끝: {ImageGradientEndSlider.Value:0}%";
        UpdateImageGradientControls();
        // 출력 섹션: 움직이는 이미지(애니/동영상)일 때만 노출. 값은 실효(지정 또는 원본) 출력값으로 채운다.
        var moving = (image.Kind == MediaKind.Animated && image.Player != null && image.Player.FrameCount > 1)
                     || image.Kind == MediaKind.Video;
        ImageOutputGroup.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        if (moving)
        {
            var (effDur, effFps) = EffectiveOutput(image);
            ImageOutputDurationBox.Text = $"{effDur:0.##}";
            ImageOutputFpsBox.Text = $"{(int)Math.Round(effFps)}";
        }
        _isLoadingInspector = false;
        UpdateSelectionLabels();
        UpdateSelectionVisuals();
    }

    private void ApplyPanelValues(ComicPanel panel)
    {
        var dW = PanelWidthSlider.Value - panel.Frame.Width;
        var dH = PanelHeightSlider.Value - panel.Frame.Height;
        panel.Frame.Width = PanelWidthSlider.Value;
        panel.Frame.Height = PanelHeightSlider.Value;
        ApplyPivotShift(panel, dW, dH);
        UpdatePanelImageSizes(panel);
        SetPanelPosition(panel, PanelXSlider.Value, PanelYSlider.Value);
        UpdateFreeBubblesForPanel(panel);
    }

    private void ApplyBubbleValues(SpeechBubble bubble)
    {
        bubble.Container.Width = BubbleWidthSlider.Value;
        bubble.Container.Height = BubbleHeightSlider.Value;
        // 글자 크기는 BubbleFontBox(숫자 입력)가 별도로 관리한다.
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
        // 텍스트박스가 줄바꿈을 정규화할 수 있으므로 런·Text를 보이는 텍스트에 맞춘다.
        if ((bubble.TextBlock.Text ?? string.Empty) != SelectedBubbleTextBox.Text)
        {
            bubble.TextBlock.StyledRuns = SpliceRuns(
                bubble.TextBlock.StyledRuns ?? MakeDefaultRuns(bubble.TextBlock.Text ?? string.Empty),
                bubble.TextBlock.Text ?? string.Empty, SelectedBubbleTextBox.Text);
            bubble.TextBlock.Text = SelectedBubbleTextBox.Text;
        }
        BubbleCropCheckBox.IsChecked = bubble.IsCropped;
        BubbleLockCheckBox.IsChecked = bubble.IsLocked;
        BubblePivotXBox.Text = $"{bubble.PivotX:0.##}";
        BubblePivotYBox.Text = $"{bubble.PivotY:0.##}";
        SelectBubbleColorInCombo(BubbleFillColorComboBox, bubble.TextBlock.Fill);
        SelectBubbleColorInCombo(BubbleStrokeColorComboBox, bubble.TextBlock.Stroke); // 투명이면 '없음'
        SelectBubbleColorInCombo(BubbleBackgroundColorComboBox, bubble.BackgroundBrush);
        SelectBubbleColorInCombo(BubbleBorderColorComboBox, new SolidColorBrush(bubble.BorderColor));
        // 돌기 최대값은 모양에 따라 다르므로(파도/외침·플래시=1000) 값 클램프 전에 먼저 맞춘다.
        BubbleShapeCountSlider.Maximum = bubble.Shape == BubbleShape.Flash ? 1000 : 100;
        BubbleShapeCountSlider.Value = Math.Clamp(bubble.ShapeCount, BubbleShapeCountSlider.Minimum, BubbleShapeCountSlider.Maximum);
        BubbleShapeStrengthSlider.Value = Math.Clamp(bubble.ShapeStrength, BubbleShapeStrengthSlider.Minimum, BubbleShapeStrengthSlider.Maximum);
        BubbleShapeIrregularitySlider.Value = Math.Clamp(bubble.ShapeIrregularity, BubbleShapeIrregularitySlider.Minimum, BubbleShapeIrregularitySlider.Maximum);
        BubbleShapeWidthVarSlider.Value = Math.Clamp(bubble.ShapeWidthVariation, BubbleShapeWidthVarSlider.Minimum, BubbleShapeWidthVarSlider.Maximum);
        // 로딩 중엔 슬라이더 ValueChanged가 안 탈 수 있어 라벨을 직접 설정한다.
        BubbleShapeStrengthText.Text = $"{StrengthOptionName(bubble.Shape)}: {BubbleShapeStrengthSlider.Value:0}";
        BubbleShapeCountText.Text = $"돌기: {BubbleShapeCountSlider.Value:0}";
        BubbleShapeIrregularityText.Text = $"불규칙도(깎임): {BubbleShapeIrregularitySlider.Value:0}";
        BubbleShapeWidthVarText.Text = $"불규칙도(폭): {BubbleShapeWidthVarSlider.Value:0}";
        BubbleTextRotationSlider.Value = Math.Clamp(bubble.TextRotation, BubbleTextRotationSlider.Minimum, BubbleTextRotationSlider.Maximum);
        BubbleTextRotationText.Text = $"텍스트 회전: {BubbleTextRotationSlider.Value:0}°";
        BubbleLineFadeBothSidesCheckBox.IsChecked = bubble.LineFadeBothSides;
        UpdateBubbleShapeOptionVisibility(bubble.Shape);
        // '안으로 깎기'는 선택한 꼬리 개별 값이므로, 선택된 꼬리가 있으면 그 값으로(없으면 해제).
        BubbleTailInwardCheckBox.IsChecked = _selectedBubbleTail?.TailInward == true;
        BubbleThoughtTailCheckBox.IsChecked = _selectedBubbleTail?.ThoughtTail == true;
        BubbleShapeComboBox.SelectedIndex = bubble.Shape switch
        {
            BubbleShape.CloudExplosion => 1,
            BubbleShape.Flash => 2,
            BubbleShape.ConcentrationLines => 3,
            BubbleShape.EffectLines => 4,
            BubbleShape.None => 5,
            _ => 0
        };
        BubbleWidthSlider.Value = bubble.Container.Width;
        BubbleHeightSlider.Value = bubble.Container.Height;
        BubbleFontBox.Text = $"{bubble.MaxFontSize:0}";
        BubbleLineHeightBox.Text = $"{bubble.TextBlock.LineHeight:0.##}";
        SelectBubbleFontInCombo(bubble.TextBlock.FontFamily?.Source ?? "Malgun Gothic");
        var bubbleAlign = bubble.TextBlock.TextAlignment.ToString();
        foreach (ComboBoxItem item in BubbleAlignmentComboBox.Items)
        {
            item.IsSelected = (item.Tag as string) == bubbleAlign;
        }
        var bubbleVAlign = bubble.TextBlock.VerticalAlignment.ToString();
        foreach (ComboBoxItem item in BubbleVAlignComboBox.Items)
        {
            item.IsSelected = (item.Tag as string) == bubbleVAlign;
        }
        BubbleWarpShapeCheckBox.IsChecked = bubble.WarpShape;
        BubbleWarpTextCheckBox.IsChecked = bubble.WarpText;
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
                PositionImageSelectionBox();                // 이동·배율이 바뀌었으니 선택 박스·핸들도 따라가게.
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
                // 변형(모서리) 리셋: 변위 0으로, 테두리·글자 워프 토글 끔.
                for (var i = 0; i < _selectedBubble.CornerOffsets.Length; i++)
                {
                    _selectedBubble.CornerOffsets[i] = new Point();
                }
                _selectedBubble.WarpShape = false;
                _selectedBubble.WarpText = false;
                _historyDirty = true;
                UpdateBubbleGeometry(_selectedBubble); // 워프 해제·텍스트 워프 파라미터 갱신.
                LoadBubbleValues(_selectedBubble);     // 인스펙터(여백 핸들·워프 체크박스) 동기화.
                PositionSelectedTailHandles();         // 텍스트/모서리 핸들 표시 갱신.
                UpdateStatus("말풍선 텍스트 여백과 변형을 기본값으로 되돌렸습니다.");
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
            // 파일이 없어 못 띄운 이미지도 목록에 보여 준다(존재 사실을 알 수 있게 — 데이터는 보존됨). 선택해 삭제 가능.
            foreach (var missing in panel.UnresolvedImages)
            {
                ImageListBox.Items.Add(new UnresolvedImageItem(panel, missing));
            }
        }

        ImageListBox.SelectedItem = _selectedImage != null && _selectedImage.OwnerPanel == panel
            ? _selectedImage
            : null;
        _isUpdatingImageList = false;
    }

    private void ClearSelection(bool announce = true)
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
        if (announce)
        {
            UpdateStatus("선택을 해제했습니다.");
        }
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
            // 칸 테두리(4선)는 '칸'이 활성 선택일 때만 강조한다(잠금 시 빨강). 비선택은 칸의 실제 테두리색.
            var isSelectedPanel = _selectionKind == SelectionKind.Panel && panel == _selectedPanel;
            var borderBrush = isSelectedPanel
                ? (panel.IsLocked ? SelectionLockedBrush : SelectionAccentBrush)
                : new SolidColorBrush(panel.BorderColor);
            foreach (var line in panel.QuadBorderLines)
            {
                line.Stroke = borderBrush;
            }

            // 칸 리사이즈는 PageOverlay의 8방향 핸들(_panelResizeHandles)이 담당하므로 칸 안의 단일 핸들은 항상 숨긴다.
            panel.ResizeHandle.Visibility = Visibility.Hidden;

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

        // 오브젝트(칸/이미지/말풍선)가 선택되면 페이지 강조를 끈다(페이지와 동시 강조 방지).
        if (_selectionKind != SelectionKind.None)
        {
            SetPageSelected(false);
        }

        // 아무것도 선택되지 않았으면 이미지/말풍선 섹션(리스트 포함)을 통째로 숨긴다.
        // (칸/이미지/말풍선 중 무언가 선택되면 해당 칸 맥락이 있으므로 두 섹션을 보인다.)
        var hasSelection = _selectionKind != SelectionKind.None;
        SetVisible(ImageSectionBorder, hasSelection);
        SetVisible(BubbleSectionBorder, hasSelection);

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
        _pendingSelect = null;
        _pendingCycle = null;

        if (_selectedPanel != null)
        {
            _selectedPanel.Frame.Cursor = Cursors.SizeAll;
        }
    }

}
