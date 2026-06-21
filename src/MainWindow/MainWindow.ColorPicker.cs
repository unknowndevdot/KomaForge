using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KomaForge;

public partial class MainWindow
{
    // 최근 사용한 임의 색(최신순). 환경설정에 저장되어 다음 실행에도 유지된다.
    private readonly List<string> _recentColors = new();
    private const int MaxRecentColors = 12;

    // '직접 지정…' 항목 태그(이 항목을 고르면 색 선택기가 열린다).
    private const string CustomColorTag = "__custom__";

    // 콤보 재구성/프로그램적 선택 중에는 SelectionChanged의 색 적용을 막는다.
    private bool _suppressColorCombo;

    // 색 콤보와 '없음(투명)' 포함 여부 등록.
    private readonly List<(ComboBox Combo, bool IncludeNone)> _colorCombos = new();

    private void InitColorCombos()
    {
        _colorCombos.Clear();
        _colorCombos.Add((BubbleFillColorComboBox, false));
        _colorCombos.Add((BubbleStrokeColorComboBox, true));
        _colorCombos.Add((BubbleBackgroundColorComboBox, true));
        _colorCombos.Add((ImageGradientColorComboBox, true)); // 투명 포함(투명=이미지 사라짐)
        _colorCombos.Add((PanelBackgroundColorComboBox, true));  // 칸 배경(없음=투명)
        _colorCombos.Add((PanelBorderColorComboBox, true));      // 칸 테두리(없음=투명)
        _colorCombos.Add((PageBackgroundColorComboBox, true));   // 페이지 배경(투명 포함 → 투명 내보내기)
        _colorCombos.Add((BubbleBorderColorComboBox, false));    // 말풍선 테두리(단색)

        RebuildColorCombos();

        SelectComboColor(BubbleFillColorComboBox, "#000000");
        SelectComboColor(BubbleStrokeColorComboBox, TransparentHex); // 기본 아웃라인 '없음'
        SelectComboColor(BubbleBackgroundColorComboBox, "#FFFFFF");
        SelectComboColor(ImageGradientColorComboBox, TransparentHex); // 기본 투명(사라짐)
        SelectComboColor(PanelBackgroundColorComboBox, "#FFFFFF");    // 기본 흰색
        SelectComboColor(PanelBorderColorComboBox, "#000000");       // 기본 검정
        SelectComboColor(PageBackgroundColorComboBox, "#FFFFFF");     // 기본 흰색
        SelectComboColor(BubbleBorderColorComboBox, "#000000");      // 기본 검정
    }

    // 모든 색 콤보를 [없음?]+[최근색]+[팔레트]+[직접 지정…] 순으로 다시 채운다(현재 선택은 유지).
    private void RebuildColorCombos()
    {
        var prev = _suppressColorCombo;
        _suppressColorCombo = true;

        foreach (var (combo, includeNone) in _colorCombos)
        {
            var currentTag = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            combo.Items.Clear();

            if (includeNone)
            {
                combo.Items.Add(MakeColorSwatchItem("없음", TransparentHex));
            }

            foreach (var hex in _recentColors)
            {
                combo.Items.Add(MakeColorSwatchItem(hex, hex, removable: true));
            }

            foreach (var (name, hex) in ColorPalette)
            {
                combo.Items.Add(MakeColorSwatchItem(name, hex));
            }

            combo.Items.Add(new ComboBoxItem { Content = "직접 지정…", Tag = CustomColorTag });

            if (currentTag != null)
            {
                SelectComboColor(combo, currentTag);
            }
        }

        _suppressColorCombo = prev;
    }

    private ComboBoxItem MakeColorSwatchItem(string label, string hex, bool removable = false)
    {
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            Background = new SolidColorBrush(SafeColor(hex)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(swatch, Dock.Left);
        content.Children.Add(swatch);

        if (removable)
        {
            // 최근색만: ✕ 버튼으로 목록에서 제거(선택은 발생하지 않게 e.Handled).
            var removeBtn = new Button
            {
                Content = "✕",
                FontSize = 10,
                Width = 16,
                Height = 16,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x93, 0x88)),
                Cursor = Cursors.Hand,
                ToolTip = "최근색에서 제거",
                VerticalAlignment = VerticalAlignment.Center
            };
            removeBtn.Click += (_, e) => { e.Handled = true; RemoveRecentColor(hex); };
            DockPanel.SetDock(removeBtn, Dock.Right);
            content.Children.Add(removeBtn);
        }

        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return new ComboBoxItem { Content = content, Tag = hex };
    }

    // 최근색에서 한 색을 제거한다(콤보 재구성은 클릭 처리 후로 미뤄 항목 변경 충돌을 피한다).
    private void RemoveRecentColor(string hex)
    {
        _recentColors.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
        Dispatcher.BeginInvoke(new Action(RebuildColorCombos), System.Windows.Threading.DispatcherPriority.Background);
    }

    // 최근색에 추가(팔레트에 이미 있으면 무시). 추가되면 모든 콤보를 다시 채운다.
    private void AddRecentColor(string hex)
    {
        hex = hex.ToUpperInvariant();
        if (ColorPalette.Any(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _recentColors.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
        _recentColors.Insert(0, hex);
        if (_recentColors.Count > MaxRecentColors)
        {
            _recentColors.RemoveRange(MaxRecentColors, _recentColors.Count - MaxRecentColors);
        }

        RebuildColorCombos();
    }

    // 말풍선 색 콤보 변경(선택된 말풍선이 있을 때만).
    private void OnBubbleColorComboChanged(ComboBox combo, Func<Color> getCurrent, Action<Color> apply, Color fallback)
        => OnColorComboChanged(combo, _selectedBubble != null, getCurrent, apply, fallback);

    // 콤보 선택 변경 공통 처리: '직접 지정…'이면 색 선택기를 열고, 그 외엔 선택색을 적용한다.
    // active = 적용 대상(말풍선/이미지 등)이 선택돼 있는지.
    private void OnColorComboChanged(ComboBox combo, bool active, Func<Color> getCurrent, Action<Color> apply, Color fallback)
    {
        if (_isLoadingInspector || _suppressColorCombo || !active)
        {
            return;
        }

        var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag == CustomColorTag)
        {
            var current = getCurrent();
            var picked = ShowColorPickerDialog(current);
            var finalColor = picked ?? current;

            // 완전 투명(알파 0)은 '없음' 항목으로 표시되므로 최근색에 넣지 않는다.
            if (picked is Color pc && !(pc.A == 0 && ComboIncludesNone(combo)))
            {
                AddRecentColor(ColorToHex(pc));
            }

            // 선택을 실제 색 항목으로 되돌린다(취소면 이전 색, 확인이면 고른 색).
            _suppressColorCombo = true;
            SelectComboColor(combo, ColorTagFor(combo, finalColor));
            _suppressColorCombo = false;

            apply(finalColor);
            return;
        }

        apply(GetComboColor(combo, fallback));
    }

    // 불러오기/선택 시 콤보에 색을 표시한다(콤보에 없는 임의 색이면 최근색으로 추가해 보이게 함).
    private void SelectBubbleColorInCombo(ComboBox combo, Brush? brush)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Black;
        if (color.A == 0 && ComboIncludesNone(combo))
        {
            SelectComboColor(combo, TransparentHex);
            return;
        }

        var hex = ColorToHex(color);
        if (!ComboHasTag(combo, hex))
        {
            AddRecentColor(hex); // 콤보 재구성
        }

        SelectComboColor(combo, hex);
    }

    private string ColorTagFor(ComboBox combo, Color c)
        => c.A == 0 && ComboIncludesNone(combo) ? TransparentHex : ColorToHex(c);

    private static bool ComboHasTag(ComboBox combo, string hex)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is string t && string.Equals(t, hex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // 불투명이면 #RRGGBB, 알파가 있으면 #AARRGGBB. ColorConverter가 둘 다 파싱한다.
    private static string ColorToHex(Color c)
        => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private bool ComboIncludesNone(ComboBox combo)
        => _colorCombos.Any(c => c.Combo == combo && c.IncludeNone);

    private static Color SafeColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Black;
        }
    }

    // RGB 슬라이더 + Hex 입력 + 미리보기로 임의 색을 고르는 모달 대화상자. 취소면 null.
    private Color? ShowColorPickerDialog(Color initial)
    {
        Color? result = null;
        byte r = initial.R, g = initial.G, b = initial.B, a = initial.A;
        var updating = false;

        // 미리보기: 체커 배경 위에 현재 색을 올려 알파(투명)가 보이게 한다.
        var colorLayer = new Border { Background = new SolidColorBrush(Color.FromArgb(a, r, g, b)) };
        var preview = new Border
        {
            Height = 44,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 14),
            Background = CreateCheckerBrush(),
            Child = colorLayer
        };

        var hexBox = new TextBox
        {
            Width = 120,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = ColorToHex(Color.FromArgb(a, r, g, b))
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(preview);

        Slider? rs = null, gs = null, bs = null, as_ = null;

        void Apply()
        {
            colorLayer.Background = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            if (!updating)
            {
                updating = true;
                hexBox.Text = ColorToHex(Color.FromArgb(a, r, g, b));
                updating = false;
            }
        }

        Slider AddChannel(string name, byte val)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var lbl = new TextBlock
            {
                Text = name,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x51, 0x4A))
            };
            var valText = new TextBlock
            {
                Width = 32,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Text = val.ToString()
            };
            var sl = new Slider { Minimum = 0, Maximum = 255, Value = val, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(lbl, Dock.Left);
            DockPanel.SetDock(valText, Dock.Right);
            row.Children.Add(lbl);
            row.Children.Add(valText);
            row.Children.Add(sl);
            panel.Children.Add(row);

            sl.ValueChanged += (_, e) =>
            {
                var v = (byte)Math.Round(e.NewValue);
                valText.Text = v.ToString();
                if (updating)
                {
                    return;
                }

                if (name == "R") r = v;
                else if (name == "G") g = v;
                else if (name == "B") b = v;
                else a = v;
                Apply();
            };
            return sl;
        }

        rs = AddChannel("R", r);
        gs = AddChannel("G", g);
        bs = AddChannel("B", b);
        as_ = AddChannel("A", a);

        var hexRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var hexLbl = new TextBlock
        {
            Text = "Hex",
            Width = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x51, 0x4A))
        };
        DockPanel.SetDock(hexLbl, Dock.Left);
        hexRow.Children.Add(hexLbl);
        hexRow.Children.Add(hexBox);
        panel.Children.Add(hexRow);

        hexBox.TextChanged += (_, _) =>
        {
            if (updating)
            {
                return;
            }

            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hexBox.Text.Trim());
                updating = true;
                r = c.R; g = c.G; b = c.B; a = c.A;
                rs!.Value = r; gs!.Value = g; bs!.Value = b; as_!.Value = a;
                colorLayer.Background = new SolidColorBrush(Color.FromArgb(a, r, g, b));
                updating = false;
            }
            catch
            {
                // 잘못된 Hex는 무시(계속 입력 중일 수 있음).
            }
        };

        var okBtn = new Button { Content = "확인", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "취소", MinWidth = 72, Margin = new Thickness(0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "색 선택",
            SizeToContent = SizeToContent.Height,
            Width = 320,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            Content = panel
        };
        okBtn.Click += (_, _) => { result = Color.FromArgb(a, r, g, b); dialog.DialogResult = true; };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;

        dialog.ShowDialog();
        return result;
    }

    // 투명 미리보기용 체커보드 브러시(흰색 + 옅은 회색 8px 격자).
    private static Brush CreateCheckerBrush()
    {
        var gray = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
