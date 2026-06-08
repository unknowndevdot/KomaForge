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

}
