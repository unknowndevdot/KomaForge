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
        // 폭의 절반 크기 정사각형을, 현재 보이는 화면 중앙에 만든다.
        var size = _pageWidth / 2.0;
        var center = CurrentViewportCenterInPage();
        var panel = CreatePanel(_nextPanelNumber++, center.X - size / 2.0, center.Y - size / 2.0, size, size);
        AddPanel(panel);
        SelectPanel(panel);
        UpdateStatus($"{panel.Number}번 칸을 추가했습니다.");
    }

    // 현재 스크롤 뷰포트의 중앙을 페이지(PanelCanvas) 좌표로 환산한다(스크롤·확대·페이지 맞춤 반영).
    private Point CurrentViewportCenterInPage()
    {
        try
        {
            if (PageScrollViewer != null && PanelCanvas != null &&
                PageScrollViewer.ViewportWidth > 0 && PageScrollViewer.ViewportHeight > 0)
            {
                var viewportCenter = new Point(PageScrollViewer.ViewportWidth / 2, PageScrollViewer.ViewportHeight / 2);
                return PageScrollViewer.TransformToDescendant(PanelCanvas).Transform(viewportCenter);
            }
        }
        catch
        {
            // 시각 트리 변환 실패 시 페이지 중앙으로.
        }

        return new Point(_pageWidth / 2.0, _pageHeight / 2.0);
    }

    private void DeletePanel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPanel == null)
        {
            UpdateStatus("삭제할 칸을 선택하세요.");
            return;
        }

        DeleteSelectedPanel();
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
                ScrollInspectorToSection();
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
        // 파일이 없는(미해결) 이미지가 선택돼 있으면 그 보관 데이터를 제거한다(칸 선택 여부와 무관).
        if (ImageListBox.SelectedItem is UnresolvedImageItem missing)
        {
            missing.Panel.UnresolvedImages.Remove(missing.Data);
            _historyDirty = true;
            UpdateImageList(missing.Panel);
            UpdateStatus("없는 이미지 항목을 삭제했습니다.");
            return;
        }

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
            CurrentFontInput(),
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
        ScrollInspectorToSection();
        RefreshCurrentPageLabel(); // 비주얼 노벨 모드 페이지 목록 요약 갱신.
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

        // 새 꼬리는 본체 아래쪽 중앙에서 곧게 내려가는 '일자' 모양으로, 세 핸들(시작·중간·끝)을 넉넉히 띄워 배치한다.
        // (기존엔 끝점이 슬라이더 값이라 위치가 들쭉날쭉하고 핸들이 붙어 나오는 경우가 많았다.)
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;
        // 여러 개 추가해도 서로의 핸들과 겹치지 않도록 중앙을 기준으로 번갈아 가로로 옮긴다(0, -30, +30, -60, …).
        var n = _selectedBubble.Tails.Count;
        var cx = w / 2 + ((n % 2 == 0) ? 1 : -1) * ((n + 1) / 2) * 30.0;
        var startY = h * 0.72;                    // 본체 안(아래쪽)에서 시작 → 본체와 자연스럽게 합쳐짐
        // 끝점은 '시작점↔하단 중앙 리사이즈 핸들(y=h)' 거리(= h - startY)만큼 그 핸들에서 더 내려간 위치.
        var tipY = h + (h - startY);
        var tail = new BubbleTail
        {
            StartX = cx,
            StartY = startY,
            MidX = cx,
            // 중간 핸들을 시작점과 하단 중앙 리사이즈 핸들(y=h) '사이'(중점)에 둔다 → 리사이즈 핸들과 겹치지 않게.
            MidY = (startY + h) / 2,
            X = cx,
            Y = tipY,
            Width = 15 // 꼬리 굵기 기본값.
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

    // 새 말풍선에 적용할 마지막 사용 글꼴(기본 Malgun Gothic).
    private string _lastBubbleFontFamily = "Malgun Gothic";

    // 시스템 글꼴 목록으로 말풍선 글꼴 콤보를 채운다(각 항목을 자기 글꼴로 미리보기).
    private void InitBubbleFontCombo()
    {
        if (BubbleFontFamilyComboBox == null)
        {
            return;
        }

        var families = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase);

        BubbleFontFamilyComboBox.Items.Clear();
        foreach (var name in families)
        {
            BubbleFontFamilyComboBox.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock { Text = name, FontFamily = new FontFamily(name) },
                Tag = name
            });
        }
    }

    private void SelectBubbleFontInCombo(string family)
    {
        foreach (ComboBoxItem item in BubbleFontFamilyComboBox.Items)
        {
            if (item.Tag is string tag && string.Equals(tag, family, StringComparison.OrdinalIgnoreCase))
            {
                BubbleFontFamilyComboBox.SelectedItem = item;
                return;
            }
        }

        BubbleFontFamilyComboBox.SelectedIndex = -1;
    }

    private void BubbleFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        if (BubbleFontFamilyComboBox.SelectedItem is ComboBoxItem item && item.Tag is string family)
        {
            _lastBubbleFontFamily = family; // 새 말풍선에 이어서 적용.
            // 선택 구간이 있으면 그 구간만, 없으면 말풍선 전체 글꼴.
            ApplyBubbleRunStyle(
                r => r.FontFamily = family,
                () => _selectedBubble!.TextBlock.FontFamily = new FontFamily(family));
        }
    }

    private void BubbleAlignmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        var align = (BubbleAlignmentComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Center";
        _selectedBubble.TextBlock.TextAlignment = ParseFlowAlignment(align);
        _historyDirty = true;
        UpdateBubbleGeometry(_selectedBubble);
    }

    private void BubbleVAlignComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        var v = (BubbleVAlignComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Center";
        _selectedBubble.TextBlock.VerticalAlignment = ParseVerticalAlignment(v);
        _historyDirty = true;
        UpdateBubbleGeometry(_selectedBubble);
    }

    private static VerticalAlignment ParseVerticalAlignment(string v) => v switch
    {
        "Top" => VerticalAlignment.Top,
        "Bottom" => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Center
    };

    private void BubbleWarpCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.WarpShape = BubbleWarpShapeCheckBox.IsChecked == true;
        _selectedBubble.WarpText = BubbleWarpTextCheckBox.IsChecked == true;
        _historyDirty = true;
        UpdateBubbleGeometry(_selectedBubble); // 도형/글자 워프 적용·해제.
        PositionSelectedTailHandles();         // 모서리 핸들 표시/숨김 갱신.
    }

    // 글자 크기 입력칸의 현재 값(없거나 잘못되면 18). 6~300으로 제한.
    private double CurrentFontInput()
        => double.TryParse(BubbleFontBox?.Text, out var v) ? Math.Clamp(v, 6, 300) : 18;

    private void BubbleFontBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        if (double.TryParse(BubbleFontBox.Text, out var v))
        {
            _selectedBubble.MaxFontSize = Math.Clamp(v, 6, 300); // 설정값은 최대치(autofit이 더 줄일 수 있음).
            UpdateBubbleGeometry(_selectedBubble);
        }
    }

    private void BubbleLineHeightBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.TextBlock.LineHeight = Math.Max(0, ParseDoubleOr(BubbleLineHeightBox.Text, 0));
        _historyDirty = true;
        UpdateBubbleGeometry(_selectedBubble); // autofit 재계산 + 재렌더.
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

    // 생각 꼬리(원형 3개) ON/OFF — 선택한 꼬리 개별 값.
    private void BubbleThoughtTail_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        if (_selectedBubbleTail == null)
        {
            UpdateStatus("생각 꼬리로 바꿀 꼬리를 먼저 선택하세요.");
            return;
        }

        _selectedBubbleTail.ThoughtTail = BubbleThoughtTailCheckBox.IsChecked == true;
        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        BubbleTailListBox.SelectedItem = _selectedBubbleTail;
        UpdateStatus(_selectedBubbleTail.ThoughtTail ? "이 꼬리를 생각 꼬리(원 3개)로 표시합니다." : "이 꼬리를 일반 곡선 꼬리로 표시합니다.");
    }

    private static readonly (string Name, string Hex)[] ColorPalette =
    {
        ("검정", "#000000"), ("흰색", "#FFFFFF"), ("회색", "#868E96"),
        ("빨강", "#E03131"), ("주황", "#F08C00"), ("노랑", "#F2CC0C"),
        ("초록", "#2F9E44"), ("파랑", "#1971C2"), ("보라", "#9C36B5"),
    };

    // 투명(아웃라인 없음)을 나타내는 특수 태그. ColorConverter가 알파 0으로 파싱한다.
    private const string TransparentHex = "#00FFFFFF";

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
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return fallback; // '직접 지정…' 같은 비색상 태그·잘못된 hex는 기본값.
            }
        }

        return fallback;
    }

    private static string ToHex(Brush? brush)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Black;
        // 불투명이면 #RRGGBB, 알파가 있으면 #AARRGGBB(반투명 색 보존).
        return color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
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
        OnBubbleColorComboChanged(BubbleFillColorComboBox,
            () => (_selectedBubble!.TextBlock.Fill as SolidColorBrush)?.Color ?? Colors.Black,
            c => ApplyBubbleRunStyle(
                r => r.Color = ColorToHex(c),
                () =>
                {
                    _selectedBubble!.TextBlock.Fill = new SolidColorBrush(c);
                    // 집중선/효과선은 선 색이 글자색을 따르므로 즉시 갱신한다.
                    UpdateBubbleShapePath(_selectedBubble);
                }),
            Colors.Black);
    }

    private void BubbleStrokeColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnBubbleColorComboChanged(BubbleStrokeColorComboBox,
            () => (_selectedBubble!.TextBlock.Stroke as SolidColorBrush)?.Color ?? Colors.Transparent,
            c => ApplyBubbleRunStyle(
                r => r.OutlineColor = ColorToHex(c),
                () => _selectedBubble!.TextBlock.Stroke = new SolidColorBrush(c)),
            Colors.Transparent);
    }

    private void BubbleBackgroundColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnBubbleColorComboChanged(BubbleBackgroundColorComboBox,
            () => (_selectedBubble!.BackgroundBrush as SolidColorBrush)?.Color ?? Colors.White,
            c =>
            {
                _selectedBubble!.BackgroundBrush = new SolidColorBrush(c);
                _selectedBubble.ShapePath.Fill = _selectedBubble.BackgroundBrush;
            },
            Colors.White);
    }

    private void PanelBackgroundColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnColorComboChanged(PanelBackgroundColorComboBox, _selectedPanel != null,
            () => (_selectedPanel!.QuadFill.Fill as SolidColorBrush)?.Color ?? Colors.White,
            c => _selectedPanel!.QuadFill.Fill = new SolidColorBrush(c),
            Colors.White);
    }

    private void PanelBorderColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnColorComboChanged(PanelBorderColorComboBox, _selectedPanel != null,
            () => _selectedPanel!.BorderColor,
            c => SetPanelBorderColor(_selectedPanel!, c),
            Colors.Black);
    }

    private void BubbleBorderColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnColorComboChanged(BubbleBorderColorComboBox, _selectedBubble != null,
            () => _selectedBubble!.BorderColor,
            c => { _selectedBubble!.BorderColor = c; UpdateMergedBubbleOutlines(_selectedBubble.OwnerPanel); },
            Colors.Black);
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

    private void ImagePivotBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedImage == null)
        {
            return;
        }

        _selectedImage.PivotX = Math.Clamp(ParseDoubleOr(ImagePivotXBox.Text, 0), 0, 1);
        _selectedImage.PivotY = Math.Clamp(ParseDoubleOr(ImagePivotYBox.Text, 1), 0, 1);
    }

    // 움직이는 이미지의 출력 시간(초)·FPS 변경. 빈/0이면 자동(원본 기준). 라이브 재생도 바로 반영.
    private void ImageOutputBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedImage == null)
        {
            return;
        }

        _selectedImage.OutputDuration = ParseDoubleOr(ImageOutputDurationBox.Text, 0) is var d && d > 0 ? d : 0;
        _selectedImage.OutputFps = ParseDoubleOr(ImageOutputFpsBox.Text, 0) is var f && f > 0 ? f : 0;
        ApplyImageOutputTiming(_selectedImage);
    }

    private void ImageGradientComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateImageGradientControls();
        if (_isLoadingInspector || _selectedImage == null)
        {
            return;
        }

        _selectedImage.GradientDirection = ParseGradientDirection((ImageGradientComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        ApplyImageGradient(_selectedImage);
    }

    private void ImageGradientColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OnColorComboChanged(ImageGradientColorComboBox, _selectedImage != null,
            () => _selectedImage!.GradientColor,
            c => { _selectedImage!.GradientColor = c; ApplyImageGradient(_selectedImage); },
            Colors.Transparent);
    }

    private void ImageGradientSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageGradientStartText != null)
        {
            ImageGradientStartText.Text = $"시작: {ImageGradientStartSlider.Value:0}%";
        }
        if (ImageGradientEndText != null)
        {
            ImageGradientEndText.Text = $"끝: {ImageGradientEndSlider.Value:0}%";
        }

        if (_isLoadingInspector || _selectedImage == null)
        {
            return;
        }

        _selectedImage.GradientStart = ImageGradientStartSlider.Value;
        _selectedImage.GradientEnd = ImageGradientEndSlider.Value;
        ApplyImageGradient(_selectedImage);
    }

    // 방향이 '없음'이면 색·게이지 항목을 숨긴다.
    private void UpdateImageGradientControls()
    {
        if (ImageGradientColorComboBox == null || ImageGradientColorLabel == null ||
            ImageGradientStartSlider == null || ImageGradientStartText == null ||
            ImageGradientEndSlider == null || ImageGradientEndText == null)
        {
            return; // XAML 초기화 중 호출 방지.
        }

        var dir = ParseGradientDirection((ImageGradientComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        var show = dir != ImageGradientDirection.None ? Visibility.Visible : Visibility.Collapsed;
        ImageGradientColorLabel.Visibility = show;
        ImageGradientColorComboBox.Visibility = show;
        ImageGradientStartText.Visibility = show;
        ImageGradientStartSlider.Visibility = show;
        ImageGradientEndText.Visibility = show;
        ImageGradientEndSlider.Visibility = show;
    }

    private void BubblePivotBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.PivotX = Math.Clamp(ParseDoubleOr(BubblePivotXBox.Text, 0), 0, 1);
        _selectedBubble.PivotY = Math.Clamp(ParseDoubleOr(BubblePivotYBox.Text, 1), 0, 1);
    }

    private void BubbleShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.Shape = GetSelectedBubbleShape();
        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleShapeOptionVisibility(_selectedBubble.Shape);
        // 리스트 표시 이름이 모양을 따르므로 콤보 변경 시 갱신한다.
        var keep = BubbleListBox.SelectedItem;
        BubbleListBox.Items.Refresh();
        BubbleListBox.SelectedItem = keep;
    }

    // 모양마다 의미 있는 옵션만 보여 준다.
    //  - 원형/사각: 강도만(타원↔사각)
    //  - 구름/폭발: 강도·돌기·불규칙도·폭 불규칙도
    //  - 파도/외침·플래시·집중선·속도선: 강도·돌기·불규칙도
    //  - 테두리 없음: 옵션 없음
    private void UpdateBubbleShapeOptionVisibility(BubbleShape shape)
    {
        var hasStrength = shape != BubbleShape.None;
        var hasCountAndIrregularity = shape is BubbleShape.CloudExplosion
            or BubbleShape.Flash
            or BubbleShape.ConcentrationLines or BubbleShape.EffectLines;
        var hasWidthVar = shape == BubbleShape.CloudExplosion;

        // 플래시는 돌기를 1000까지 허용(그 외는 100).
        BubbleShapeCountSlider.Maximum = shape == BubbleShape.Flash ? 1000 : 100;

        SetShapeOptionVisible(BubbleShapeStrengthText, BubbleShapeStrengthSlider, hasStrength);
        // 모양에 따라 라벨 이름이 달라지므로(속도선=회전, 그 외=강도) 모양 변경 즉시 라벨을 갱신한다.
        if (BubbleShapeStrengthText != null)
        {
            BubbleShapeStrengthText.Text = $"{StrengthOptionName(shape)}: {BubbleShapeStrengthSlider.Value:0}";
        }
        SetShapeOptionVisible(BubbleShapeCountText, BubbleShapeCountSlider, hasCountAndIrregularity);
        SetShapeOptionVisible(BubbleShapeIrregularityText, BubbleShapeIrregularitySlider, hasCountAndIrregularity);
        SetShapeOptionVisible(BubbleShapeWidthVarText, BubbleShapeWidthVarSlider, hasWidthVar);
        // 양쪽 페이드는 속도선에서만 의미가 있다.
        if (BubbleLineFadeBothSidesCheckBox != null)
        {
            BubbleLineFadeBothSidesCheckBox.Visibility =
                shape == BubbleShape.EffectLines ? Visibility.Visible : Visibility.Collapsed;
        }
        // 글자 회전은 선효과(속도선·집중선)를 뺀 모든 말풍선에서 보여 준다.
        SetShapeOptionVisible(BubbleTextRotationText, BubbleTextRotationSlider, !IsLineEffectShape(shape));

        // 꼬리는 본체가 있는 모양(원형/사각·구름폭발·플래시)에만 의미가 있다. 집중선·속도선·테두리 없음은 꼬리 섹션을 숨긴다.
        var hasTail = shape is BubbleShape.RoundRect or BubbleShape.CloudExplosion or BubbleShape.Flash;
        if (BubbleTailSectionBorder != null)
        {
            BubbleTailSectionBorder.Visibility = hasTail ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // 강도 슬라이더의 모양별 표시 이름(속도선은 방향이라 '회전').
    private static string StrengthOptionName(BubbleShape shape)
        => shape == BubbleShape.EffectLines ? "회전" : "강도";

    private static void SetShapeOptionVisible(UIElement label, UIElement slider, bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        if (label != null) label.Visibility = v;
        if (slider != null) slider.Visibility = v;
    }

    private void BubbleShapeStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleShapeStrengthText != null)
        {
            var name = StrengthOptionName(_selectedBubble?.Shape ?? BubbleShape.RoundRect);
            BubbleShapeStrengthText.Text = $"{name}: {BubbleShapeStrengthSlider.Value:0}";
        }

        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.ShapeStrength = BubbleShapeStrengthSlider.Value;
        UpdateBubbleGeometry(_selectedBubble);
    }

    private void BubbleTextRotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleTextRotationText != null)
        {
            BubbleTextRotationText.Text = $"텍스트 회전: {BubbleTextRotationSlider.Value:0}°";
        }

        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.TextRotation = BubbleTextRotationSlider.Value;
        UpdateBubbleGeometry(_selectedBubble);
    }

    // 속도선 양쪽 페이드 ON/OFF.
    private void BubbleLineFadeBothSides_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.LineFadeBothSides = BubbleLineFadeBothSidesCheckBox.IsChecked == true;
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

    private void BubbleShapeIrregularitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleShapeIrregularityText != null)
        {
            BubbleShapeIrregularityText.Text = $"불규칙도(깎임): {BubbleShapeIrregularitySlider.Value:0}";
        }

        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.ShapeIrregularity = BubbleShapeIrregularitySlider.Value;
        UpdateBubbleGeometry(_selectedBubble);
    }

    private void BubbleShapeWidthVarSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleShapeWidthVarText != null)
        {
            BubbleShapeWidthVarText.Text = $"불규칙도(폭): {BubbleShapeWidthVarSlider.Value:0}";
        }

        if (_isLoadingInspector || _selectedBubble == null)
        {
            return;
        }

        _selectedBubble.ShapeWidthVariation = BubbleShapeWidthVarSlider.Value;
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

        var tb = _selectedBubble.TextBlock;
        var oldText = tb.Text ?? string.Empty;
        // 편집을 '한 구간 치환'으로 보고 구간 서식을 보존한다(본문 텍스트와 동일 로직 재사용).
        tb.StyledRuns = SpliceRuns(tb.StyledRuns ?? MakeDefaultRuns(oldText), oldText, SelectedBubbleTextBox.Text);
        tb.Text = SelectedBubbleTextBox.Text;
        _historyDirty = true;
        // 텍스트가 길어지면 말풍선에 맞춰 글자 크기를 자동 축소한다.
        ApplyBubbleAutoFit(_selectedBubble);
        RefreshCurrentPageLabel(); // 비주얼 노벨 모드면 페이지 목록 요약 즉시 갱신.
    }

    // 선택 구간이 있으면 그 구간의 런에 set을 적용하고, 없으면 말풍선 전체 기본값(setBase)을 바꾼다.
    private void ApplyBubbleRunStyle(System.Action<FlowTextRun> set, System.Action setBase)
    {
        if (_selectedBubble == null)
        {
            return;
        }

        var tb = _selectedBubble.TextBlock;
        if (SelectedBubbleTextBox != null && SelectedBubbleTextBox.SelectionLength > 0)
        {
            var runs = tb.StyledRuns is { Count: > 0 } ? tb.StyledRuns : MakeDefaultRuns(tb.Text);
            var start = SelectedBubbleTextBox.SelectionStart;
            var len = SelectedBubbleTextBox.SelectionLength;
            tb.StyledRuns = ApplyStyleToRange(runs, start, start + len, set);
            _historyDirty = true;
            UpdateBubbleGeometry(_selectedBubble);
            SelectedBubbleTextBox.Focus();
            SelectedBubbleTextBox.Select(start, len); // 선택 유지(연이어 다른 서식 적용 가능).
        }
        else
        {
            setBase();
            _historyDirty = true;
            UpdateBubbleGeometry(_selectedBubble);
        }
    }

}
