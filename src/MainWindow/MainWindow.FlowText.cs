using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KomaForge;

public partial class MainWindow : Window
{
    // 프로젝트 전체에 걸쳐 페이지별로 흘려 표시하는 본문 텍스트(노벨 뷰어). 칸·이미지 위 레이어.
    private FlowTextData _flow = new();
    private FlowDocument? _flowDoc;
    private DocumentPaginator? _flowPaginator;
    private PageVisualHost? _flowHost;
    // 렌더 키색 → (실제 채움색, 아웃라인색) 매핑. 구간마다 고유 키색을 부여해 같은 글자색이라도
    // 아웃라인을 독립적으로 그룹·렌더한다(키색은 그리지 않으므로 화면에 안 보임). RebuildFlowDocument에서 갱신.
    private System.Collections.Generic.Dictionary<Color, (Color Fill, Color Outline)> _flowRenderMap = new();
    private bool _flowNeedOutline; // 불투명 아웃라인이 하나라도 있으면 그룹 경로로 렌더.

    // 분할된 한 페이지의 Visual을 그대로 호스팅하는 경량 요소(DocumentPageView가 public이 아니라 직접 구현).
    private sealed class PageVisualHost : FrameworkElement
    {
        private Visual? _child;

        public void SetChild(Visual? v)
        {
            if (ReferenceEquals(_child, v))
            {
                return;
            }
            if (_child != null)
            {
                RemoveVisualChild(_child);
            }
            _child = v;
            if (_child != null)
            {
                AddVisualChild(_child);
            }
            InvalidateMeasure();
        }

        protected override int VisualChildrenCount => _child == null ? 0 : 1;
        protected override Visual GetVisualChild(int index) => _child!;
    }

    private void EnsureFlowHost()
    {
        if (_flowHost != null)
        {
            return;
        }

        _flowHost = new PageVisualHost
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        FlowTextLayer.Children.Add(_flowHost);
    }

    // _flow의 텍스트·서식·현재 페이지 크기로 FlowDocument를 만들어 '페이지 단위 분할'을 준비한다.
    // 텍스트·서식·페이지 크기가 바뀔 때만 호출(페이지 전환·추가/삭제 시엔 호출하지 않음 → 분할 재계산 회피).
    private void RebuildFlowDocument()
    {
        EnsureFlowHost();

        if (!_flow.Enabled || string.IsNullOrEmpty(_flow.Text))
        {
            _flowDoc = null;
            _flowPaginator = null;
            _flowHost!.SetChild(null);
            return;
        }

        var doc = new FlowDocument
        {
            PageWidth = _pageWidth,
            PageHeight = _pageHeight,
            PagePadding = new Thickness(_flow.MarginLeft, _flow.MarginTop, _flow.MarginRight, _flow.MarginBottom),
            ColumnWidth = double.PositiveInfinity, // 단일 컬럼.
            FontSize = _flow.FontSize > 0 ? _flow.FontSize : 20,
            TextAlignment = ParseFlowAlignment(_flow.Alignment),
            Foreground = new SolidColorBrush(SafeColor(string.IsNullOrEmpty(_flow.Color) ? "#000000" : _flow.Color))
        };
        if (!string.IsNullOrEmpty(_flow.FontFamily))
        {
            try { doc.FontFamily = new FontFamily(_flow.FontFamily); } catch { /* 알 수 없는 글꼴은 기본 사용 */ }
        }

        var defColor = string.IsNullOrEmpty(_flow.Color) ? "#000000" : _flow.Color;
        var defOutline = string.IsNullOrEmpty(_flow.OutlineColor) ? "#00FFFFFF" : _flow.OutlineColor;
        _flowRenderMap = new System.Collections.Generic.Dictionary<Color, (Color, Color)>();

        var runs = _flow.Runs.Count > 0 ? _flow.Runs : MakeDefaultRuns(_flow.Text);

        // 불투명 아웃라인(기본값 또는 구간)이 하나라도 있으면 그룹 경로로 렌더한다.
        _flowNeedOutline = SafeColor(defOutline).A > 0;
        if (!_flowNeedOutline)
        {
            foreach (var run in runs)
            {
                if (!string.IsNullOrEmpty(run.OutlineColor) && SafeColor(run.OutlineColor).A > 0)
                {
                    _flowNeedOutline = true;
                    break;
                }
            }
        }

        // (채움색,아웃라인색) 조합마다 고유 키색 부여(같은 채움색이라도 아웃라인이 다르면 다른 그룹).
        var pairKeys = new System.Collections.Generic.Dictionary<(Color, Color), Color>();
        var keySeq = 0;

        // 구간(런)을 순서대로 문단으로 만든다. 런 텍스트 안의 줄바꿈마다 새 문단으로 나눈다.
        // 색/글꼴은 구간이 지정했을 때만 Run에 적용하고, 없으면 문서 기본값을 상속한다.
        var para = NewFlowParagraph();
        foreach (var run in runs)
        {
            var parts = run.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    doc.Blocks.Add(para);
                    para = NewFlowParagraph();
                }
                if (parts[i].Length == 0)
                {
                    continue;
                }

                var wpfRun = new Run(parts[i]);
                var fillColor = SafeColor(string.IsNullOrEmpty(run.Color) ? defColor : run.Color);
                var outlineColor = SafeColor(string.IsNullOrEmpty(run.OutlineColor) ? defOutline : run.OutlineColor);
                if (!string.IsNullOrEmpty(run.FontFamily))
                {
                    try { wpfRun.FontFamily = new FontFamily(run.FontFamily); } catch { /* 알 수 없는 글꼴은 기본 사용 */ }
                }
                if (_flowNeedOutline)
                {
                    // 그룹 경로: 모든 구간에 고유 키색을 전경색으로 부여(키색은 그리지 않음, 실제 색은 렌더 시 사용).
                    wpfRun.Foreground = new SolidColorBrush(FlowRenderKey(fillColor, outlineColor, pairKeys, ref keySeq));
                }
                else if (!string.IsNullOrEmpty(run.Color))
                {
                    wpfRun.Foreground = new SolidColorBrush(fillColor);
                }
                para.Inlines.Add(wpfRun);
            }
        }
        doc.Blocks.Add(para);

        _flowDoc = doc;
        _flowPaginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        _flowPaginator.PageSize = new Size(_pageWidth, _pageHeight);
        UpdateFlowTextLayer();
    }

    private Paragraph NewFlowParagraph()
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        if (_flow.LineHeight > 0)
        {
            p.LineHeight = _flow.LineHeight;
        }
        return p;
    }

    // 현재 페이지 인덱스에 해당하는 분량만 표시한다. 인덱스가 분할된 페이지 수를 넘으면 빈 화면(잘림),
    // 페이지를 추가해 그 인덱스가 분량 안에 들어오면 다음 분량이 보인다.
    private void UpdateFlowTextLayer()
    {
        if (_flowHost == null)
        {
            return;
        }

        if (_flowPaginator == null || _currentPageIndex < 0)
        {
            _flowHost.SetChild(null);
            return;
        }

        try
        {
            if (!_flowPaginator.IsPageCountValid)
            {
                _flowPaginator.ComputePageCount(); // 전체 분할(최초/텍스트 변경 시 1회).
            }

            if (_currentPageIndex >= _flowPaginator.PageCount)
            {
                _flowHost.SetChild(null); // 분량 부족 → 잘려서 안 보임.
                return;
            }

            var page = _flowPaginator.GetPage(_currentPageIndex);
            _flowHost.SetChild(BuildFlowPageVisual(page.Visual));
        }
        catch
        {
            _flowHost.SetChild(null);
        }
    }

    // 저장/복원에서 받은 데이터로 본문을 적용하고(인스펙터 갱신) 다시 분할한다.
    private void ApplyFlowText(FlowTextData? data)
    {
        _flow = data?.Clone() ?? new FlowTextData();
        // 구버전 호환: 런이 없으면 Text 한 덩어리를 서식 없는 런 하나로 마이그레이션.
        if (_flow.Runs.Count == 0 && !string.IsNullOrEmpty(_flow.Text))
        {
            _flow.Runs = MakeDefaultRuns(_flow.Text);
        }
        LoadFlowInspector();
        ApplyBackdrop();
        RebuildFlowDocument();
    }

    // 페이지 뒤(PageFrame)의 배경을 정한다. 텍스트 모드 ON이면 단일 뒷배경색, OFF면 페이지 색(기존 동작).
    // PageFrame.Background의 유일한 설정 지점(ApplyPageBackground도 여기로 위임).
    private void ApplyBackdrop()
    {
        if (PageFrame == null)
        {
            return;
        }
        var color = _flow.Enabled
            ? SafeColor(string.IsNullOrEmpty(_flow.BackdropColor) ? "#FFFFFF" : _flow.BackdropColor)
            : CurrentPageBackgroundColor();
        PageFrame.Background = new SolidColorBrush(color);
    }

    private static TextAlignment ParseFlowAlignment(string a) => a switch
    {
        "Center" => TextAlignment.Center,
        "Right" => TextAlignment.Right,
        "Justify" => TextAlignment.Justify,
        _ => TextAlignment.Left
    };

    // --- 인스펙터 텍스트 섹션 ---

    private void InitFlowFontCombo()
    {
        if (FlowFontFamilyComboBox == null)
        {
            return;
        }

        var families = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s, System.StringComparer.CurrentCultureIgnoreCase);

        FlowFontFamilyComboBox.Items.Clear();
        foreach (var name in families)
        {
            FlowFontFamilyComboBox.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock { Text = name, FontFamily = new FontFamily(name) },
                Tag = name
            });
        }

        SelectFlowFontInCombo(_flow.FontFamily); // 시작 시 기본 글꼴(말풍선과 동일) 선택.
    }

    // _flow 값을 인스펙터 컨트롤에 채운다(불러오기·복원 시). 변경 이벤트는 _isLoadingInspector로 억제.
    private void LoadFlowInspector()
    {
        if (FlowTextBox == null)
        {
            return;
        }

        _isLoadingInspector = true;
        FlowTextModeCheckBox.IsChecked = _flow.Enabled;
        FlowTextSectionBorder.Visibility = _flow.Enabled ? Visibility.Visible : Visibility.Collapsed;
        FlowTextBox.Text = _flow.Text;
        // 텍스트박스가 줄바꿈을 \r\n으로 정규화할 수 있으므로, 보이는 텍스트에 런·_flow.Text를 맞춘다.
        if (RunsText(_flow.Runs) != FlowTextBox.Text)
        {
            _flow.Runs = SpliceRuns(_flow.Runs, RunsText(_flow.Runs), FlowTextBox.Text);
            _flow.Text = FlowTextBox.Text;
        }
        FlowFontSizeBox.Text = $"{_flow.FontSize:0.##}";
        FlowLineHeightBox.Text = $"{_flow.LineHeight:0.##}";
        FlowMarginLeftBox.Text = $"{_flow.MarginLeft:0.##}";
        FlowMarginTopBox.Text = $"{_flow.MarginTop:0.##}";
        FlowMarginRightBox.Text = $"{_flow.MarginRight:0.##}";
        FlowMarginBottomBox.Text = $"{_flow.MarginBottom:0.##}";
        foreach (ComboBoxItem item in FlowAlignmentComboBox.Items)
        {
            item.IsSelected = item.Tag as string == _flow.Alignment;
        }
        SelectFlowFontInCombo(_flow.FontFamily);
        SelectBubbleColorInCombo(FlowColorComboBox,
            new SolidColorBrush(SafeColor(string.IsNullOrEmpty(_flow.Color) ? "#000000" : _flow.Color)));
        SelectBubbleColorInCombo(FlowOutlineColorComboBox,
            new SolidColorBrush(SafeColor(string.IsNullOrEmpty(_flow.OutlineColor) ? TransparentHex : _flow.OutlineColor)));
        SelectBubbleColorInCombo(FlowBackdropColorComboBox,
            new SolidColorBrush(SafeColor(string.IsNullOrEmpty(_flow.BackdropColor) ? "#FFFFFF" : _flow.BackdropColor)));
        _isLoadingInspector = false;
    }

    private void SelectFlowFontInCombo(string family)
    {
        foreach (ComboBoxItem item in FlowFontFamilyComboBox.Items)
        {
            if (item.Tag is string tag && string.Equals(tag, family, System.StringComparison.OrdinalIgnoreCase))
            {
                FlowFontFamilyComboBox.SelectedItem = item;
                return;
            }
        }
        FlowFontFamilyComboBox.SelectedIndex = -1; // 기본 글꼴.
    }

    // 텍스트 모드 ON/OFF 토글(Ctrl+T). 숨겨진 체크박스를 통해 섹션 표시·본문 오버레이를 함께 갱신.
    private void ToggleTextMode()
    {
        if (FlowTextModeCheckBox == null)
        {
            return;
        }
        FlowTextModeCheckBox.IsChecked = !(FlowTextModeCheckBox.IsChecked == true);
        UpdateStatus(FlowTextModeCheckBox.IsChecked == true ? "텍스트 모드 ON" : "텍스트 모드 OFF");
    }

    private void FlowTextModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // 섹션 표시는 로딩 중에도 상태에 맞춰 갱신한다(체크박스가 토글되는 즉시 반영).
        if (FlowTextSectionBorder != null)
        {
            FlowTextSectionBorder.Visibility =
                FlowTextModeCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_flowReady || _isLoadingInspector)
        {
            return;
        }

        _flow.Enabled = FlowTextModeCheckBox.IsChecked == true;
        _historyDirty = true;
        ApplyBackdrop();       // ON/OFF에 따라 페이지 뒤 배경색을 켜거나 흰색으로 되돌린다.
        RebuildFlowDocument(); // ON/OFF에 따라 본문 오버레이를 켜거나 지운다.
    }

    private void FlowTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_flowReady || _isLoadingInspector)
        {
            return;
        }
        // 편집(타이핑·삭제·붙여넣기)을 '한 구간 치환'으로 보고 런 스팬을 그 변경만큼만 갱신해 구간 서식을 보존한다.
        _flow.Runs = SpliceRuns(_flow.Runs, _flow.Text ?? string.Empty, FlowTextBox.Text);
        _flow.Text = FlowTextBox.Text;
        _historyDirty = true;
        RebuildFlowDocument();
    }

    private void FlowStyleBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_flowReady || _isLoadingInspector)
        {
            return;
        }
        _flow.FontSize = System.Math.Clamp(ParseDoubleOr(FlowFontSizeBox.Text, 20), 1, 500);
        _flow.LineHeight = System.Math.Max(0, ParseDoubleOr(FlowLineHeightBox.Text, 0));
        _flow.MarginLeft = System.Math.Max(0, ParseDoubleOr(FlowMarginLeftBox.Text, 0));
        _flow.MarginTop = System.Math.Max(0, ParseDoubleOr(FlowMarginTopBox.Text, 0));
        _flow.MarginRight = System.Math.Max(0, ParseDoubleOr(FlowMarginRightBox.Text, 0));
        _flow.MarginBottom = System.Math.Max(0, ParseDoubleOr(FlowMarginBottomBox.Text, 0));
        _historyDirty = true;
        RebuildFlowDocument();
    }

    private void FlowAlignmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_flowReady || _isLoadingInspector)
        {
            return;
        }
        _flow.Alignment = (FlowAlignmentComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Left";
        _historyDirty = true;
        RebuildFlowDocument();
    }

    private void FlowFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_flowReady || _isLoadingInspector)
        {
            return;
        }
        var family = (FlowFontFamilyComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        // 선택 구간이 있으면 그 구간만, 없으면 문서 기본 글꼴.
        ApplyFlowSelectionStyle(
            r => r.FontFamily = string.IsNullOrEmpty(family) ? null : family,
            () => _flow.FontFamily = family);
    }

    private void FlowColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_flowReady)
        {
            return;
        }
        OnColorComboChanged(FlowColorComboBox, true,
            () => SafeColor(string.IsNullOrEmpty(_flow.Color) ? "#000000" : _flow.Color),
            c => ApplyFlowSelectionStyle(
                r => r.Color = ColorToHex(c),
                () => _flow.Color = ColorToHex(c)),
            Colors.Black);
    }

    private void FlowOutlineColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_flowReady)
        {
            return;
        }
        OnColorComboChanged(FlowOutlineColorComboBox, true,
            () => SafeColor(string.IsNullOrEmpty(_flow.OutlineColor) ? "#00FFFFFF" : _flow.OutlineColor),
            c => ApplyFlowSelectionStyle(
                r => r.OutlineColor = ColorToHex(c),
                () => _flow.OutlineColor = ColorToHex(c)),
            Colors.Transparent);
    }

    // 선택 구간이 있으면 그 구간의 런에 set을 적용하고, 없으면 문서 기본값(setDefault)을 바꾼다. 이후 재분할.
    private void ApplyFlowSelectionStyle(System.Action<FlowTextRun> set, System.Action setDefault)
    {
        if (FlowTextBox != null && FlowTextBox.SelectionLength > 0)
        {
            EnsureRuns();
            var start = FlowTextBox.SelectionStart;
            var len = FlowTextBox.SelectionLength;
            _flow.Runs = ApplyStyleToRange(_flow.Runs, start, start + len, set);
            _flow.Text = RunsText(_flow.Runs);
            _historyDirty = true;
            RebuildFlowDocument();
            FlowTextBox.Focus();
            FlowTextBox.Select(start, len); // 선택을 유지해 연이어 다른 서식을 적용할 수 있게.
        }
        else
        {
            setDefault();
            _historyDirty = true;
            RebuildFlowDocument();
        }
    }

    private void FlowBackdropColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_flowReady)
        {
            return;
        }
        OnColorComboChanged(FlowBackdropColorComboBox, true,
            () => SafeColor(string.IsNullOrEmpty(_flow.BackdropColor) ? "#FFFFFF" : _flow.BackdropColor),
            c => { _flow.BackdropColor = ColorToHex(c); _historyDirty = true; ApplyBackdrop(); },
            Colors.White);
    }

    // 본문이 모두 보이도록 페이지 수를 분할 결과(PageCount)에 맞춘다. 부족하면 빈 페이지 추가, 남으면 뒤에서 삭제.
    private void FitPagesToFlowText_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentPageState();

        if (string.IsNullOrEmpty(_flow.Text) || _flowPaginator == null)
        {
            UpdateStatus("본문 텍스트가 없습니다.");
            return;
        }

        int required;
        try
        {
            if (!_flowPaginator.IsPageCountValid)
            {
                _flowPaginator.ComputePageCount(); // 전체 분할(필요 페이지 수 산출).
            }
            required = System.Math.Max(1, _flowPaginator.PageCount);
        }
        catch
        {
            UpdateStatus("텍스트 분량을 계산할 수 없습니다.");
            return;
        }

        if (required == _pages.Count)
        {
            UpdateStatus($"이미 텍스트 분량에 맞는 {required}개 페이지입니다.");
            return;
        }

        if (required < _pages.Count)
        {
            // 줄이기: 뒤쪽 페이지를 삭제한다. 삭제 대상에 칸/내용이 있으면 한 번 확인한다(데이터 손실 방지).
            var removeCount = _pages.Count - required;
            var hasContent = false;
            for (var i = required; i < _pages.Count; i++)
            {
                if (_pages[i].Panels.Count > 0)
                {
                    hasContent = true;
                    break;
                }
            }

            if (hasContent &&
                MessageBox.Show(this,
                    $"텍스트 분량에 맞추려면 뒤쪽 페이지 {removeCount}개를 삭제해야 합니다.\n그중 일부에는 칸/내용이 있습니다. 계속할까요?",
                    "텍스트 길이에 맞춰 페이지 생성", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _historyStructuralPending = true; // 페이지 구조 변경: 다음 캡처에서 전체 재직렬화.
            for (var i = _pages.Count - 1; i >= required; i--)
            {
                _pages.RemoveAt(i);
            }
        }
        else
        {
            // 늘리기: 새 페이지는 현재 페이지의 크기·배경색·칸(수·모양·색)만 복제하고,
            // 칸 안의 내용(이미지·말풍선)은 비운다. JSON 왕복으로 깊은 복사해 페이지끼리 데이터를 공유하지 않게 한다.
            _historyStructuralPending = true;
            var templateJson = JsonSerializer.Serialize(_pages[_currentPageIndex]);
            while (_pages.Count < required)
            {
                var clone = JsonSerializer.Deserialize<ComicPageData>(templateJson)!;
                clone.Name = $"Page {_pages.Count + 1}";
                foreach (var panel in clone.Panels)
                {
                    panel.Images.Clear();  // 칸 모양만 복제 — 내용물(이미지)은 제외.
                    panel.Bubbles.Clear(); // 칸 모양만 복제 — 내용물(말풍선)은 제외.
                }
                _pages.Add(clone);
            }
        }

        _currentPageIndex = System.Math.Clamp(_currentPageIndex, 0, _pages.Count - 1);
        LoadPage(_pages[_currentPageIndex]);
        ClearSelection();
        UpdatePageList();
        UpdateStatus($"텍스트 분량에 맞춰 페이지를 {required}개로 맞췄습니다.");
    }

    // 분할된 페이지 Visual에 아웃라인을 입혀 다시 그린다. 글리프를 채움색별로 묶어 구간별 색·아웃라인을 보존한다.
    // 모든 아웃라인이 투명이면 네이티브 Visual을 그대로 쓴다(per-run 채움색은 이미 정확).
    private Visual BuildFlowPageVisual(Visual pageVisual)
    {
        if (!_flowNeedOutline)
        {
            return pageVisual; // 불투명 아웃라인 없음 → 네이티브 글리프(구간별 채움색 그대로).
        }

        var byColor = new System.Collections.Generic.Dictionary<Color, GeometryGroup>();
        CollectGlyphGeometry(pageVisual, pageVisual, byColor);
        if (byColor.Count == 0)
        {
            return pageVisual;
        }

        var penWidth = System.Math.Max(3, (_flow.FontSize > 0 ? _flow.FontSize : 20) / 3.5);
        var defFill = SafeColor(string.IsNullOrEmpty(_flow.Color) ? "#000000" : _flow.Color);
        var defOutline = SafeColor(string.IsNullOrEmpty(_flow.OutlineColor) ? "#00FFFFFF" : _flow.OutlineColor);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 1패스: 외곽선(모든 글자 뒤로 깔리도록 먼저). 그룹 키색 → 실제 (채움,아웃라인) 매핑 사용.
            foreach (var kv in byColor)
            {
                var outline = _flowRenderMap.TryGetValue(kv.Key, out var pair) ? pair.Outline : defOutline;
                if (outline.A == 0)
                {
                    continue;
                }
                var pen = new Pen(new SolidColorBrush(outline), penWidth) { LineJoin = PenLineJoin.Round, MiterLimit = 2 };
                dc.DrawGeometry(null, pen, kv.Value);
            }
            // 2패스: 채움(각 구간의 실제 채움색).
            foreach (var kv in byColor)
            {
                var fill = _flowRenderMap.TryGetValue(kv.Key, out var pair) ? pair.Fill : defFill;
                dc.DrawGeometry(new SolidColorBrush(fill), null, kv.Value);
            }
        }
        return dv;
    }

    // (채움,아웃라인) 조합마다 고유 키색을 반환한다(같은 조합은 재사용해 한 그룹으로 묶임). 키색은 그리지 않고 그룹 식별용.
    private Color FlowRenderKey(Color fill, Color outline, System.Collections.Generic.Dictionary<(Color, Color), Color> pairKeys, ref int seq)
    {
        var pk = (fill, outline);
        if (pairKeys.TryGetValue(pk, out var existing))
        {
            return existing;
        }
        var key = Color.FromArgb(255, (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq);
        seq++;
        pairKeys[pk] = key;
        _flowRenderMap[key] = (fill, outline);
        return key;
    }

    // 페이지 Visual 트리를 훑어 글리프 지오메트리를 '채움색별 그룹'으로 모은다(루트 좌표 기준).
    private void CollectGlyphGeometry(Visual root, Visual v, System.Collections.Generic.Dictionary<Color, GeometryGroup> acc)
    {
        var drawing = VisualTreeHelper.GetDrawing(v);
        if (drawing != null)
        {
            var m = ReferenceEquals(v, root) ? Matrix.Identity : AffineToAncestor(v, root);
            CollectGlyphFromDrawing(drawing, m, acc);
        }

        var n = VisualTreeHelper.GetChildrenCount(v);
        for (var i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(v, i) is Visual child)
            {
                CollectGlyphGeometry(root, child, acc);
            }
        }
    }

    private void CollectGlyphFromDrawing(Drawing d, Matrix m, System.Collections.Generic.Dictionary<Color, GeometryGroup> acc)
    {
        switch (d)
        {
            case DrawingGroup dg:
                var gm = m;
                if (dg.Transform != null && !dg.Transform.Value.IsIdentity)
                {
                    gm = dg.Transform.Value * m;
                }
                foreach (var child in dg.Children)
                {
                    CollectGlyphFromDrawing(child, gm, acc);
                }
                break;

            case GlyphRunDrawing grd when grd.GlyphRun != null:
                var geo = grd.GlyphRun.BuildGeometry();
                if (geo != null && !geo.IsEmpty())
                {
                    if (!m.IsIdentity)
                    {
                        geo.Transform = new MatrixTransform(m);
                    }
                    var color = (grd.ForegroundBrush as SolidColorBrush)?.Color
                                ?? SafeColor(string.IsNullOrEmpty(_flow.Color) ? "#000000" : _flow.Color);
                    if (!acc.TryGetValue(color, out var grp))
                    {
                        grp = new GeometryGroup { FillRule = FillRule.Nonzero };
                        acc[color] = grp;
                    }
                    grp.Children.Add(geo);
                }
                break;
        }
    }

    // 비주얼의 로컬 좌표 → 조상 좌표로 가는 아핀 행렬을 복원한다(세 점 변환에서 역산).
    private static Matrix AffineToAncestor(Visual v, Visual ancestor)
    {
        var tx = v.TransformToAncestor(ancestor);
        var p0 = tx.Transform(new Point(0, 0));
        var px = tx.Transform(new Point(1, 0));
        var py = tx.Transform(new Point(0, 1));
        return new Matrix(px.X - p0.X, px.Y - p0.Y, py.X - p0.X, py.Y - p0.Y, p0.X, p0.Y);
    }

    // --- 구간(런) 스팬 유틸 ---

    // 런이 없으면 Text로부터 만든다(구간 서식 적용 직전 보장).
    private void EnsureRuns()
    {
        if (_flow.Runs.Count == 0 && !string.IsNullOrEmpty(_flow.Text))
        {
            _flow.Runs = MakeDefaultRuns(_flow.Text);
        }
    }

    private static System.Collections.Generic.List<FlowTextRun> MakeDefaultRuns(string text)
        => string.IsNullOrEmpty(text)
            ? new System.Collections.Generic.List<FlowTextRun>()
            : new System.Collections.Generic.List<FlowTextRun> { new FlowTextRun { Text = text } };

    private static string RunsText(System.Collections.Generic.List<FlowTextRun> runs)
        => string.Concat(runs.Select(r => r.Text));

    private static bool SameFlowStyle(FlowTextRun a, FlowTextRun b)
        => (a.FontFamily ?? "") == (b.FontFamily ?? "")
           && (a.Color ?? "") == (b.Color ?? "")
           && (a.OutlineColor ?? "") == (b.OutlineColor ?? "");

    // 인접한 동일 서식 런을 합치고 빈 런을 제거한다.
    private static System.Collections.Generic.List<FlowTextRun> CoalesceRuns(System.Collections.Generic.List<FlowTextRun> runs)
    {
        var result = new System.Collections.Generic.List<FlowTextRun>();
        foreach (var r in runs)
        {
            if (r.Text.Length == 0)
            {
                continue;
            }
            if (result.Count > 0 && SameFlowStyle(result[^1], r))
            {
                result[^1].Text += r.Text;
            }
            else
            {
                result.Add(r.Clone());
            }
        }
        return result;
    }

    // runs에서 문자 구간 [from,to)를 잘라 새 런 리스트로 반환(서식 유지).
    private static System.Collections.Generic.List<FlowTextRun> SliceRuns(System.Collections.Generic.List<FlowTextRun> runs, int from, int to)
    {
        var result = new System.Collections.Generic.List<FlowTextRun>();
        if (to <= from)
        {
            return result;
        }
        var pos = 0;
        foreach (var r in runs)
        {
            var rStart = pos;
            var rEnd = pos + r.Text.Length;
            pos = rEnd;
            var a = System.Math.Max(from, rStart);
            var b = System.Math.Min(to, rEnd);
            if (b <= a)
            {
                continue;
            }
            var piece = r.Clone();
            piece.Text = r.Text.Substring(a - rStart, b - a);
            result.Add(piece);
        }
        return result;
    }

    // 문자 index 위치의 런(상속 서식 판단용). 없으면 마지막/기본.
    private static FlowTextRun StyleAt(System.Collections.Generic.List<FlowTextRun> runs, int index)
    {
        var pos = 0;
        foreach (var r in runs)
        {
            if (index < pos + r.Text.Length)
            {
                return r;
            }
            pos += r.Text.Length;
        }
        return runs.Count > 0 ? runs[^1] : new FlowTextRun();
    }

    // oldText→newText 한 구간 치환으로 보고 런을 갱신. 삽입 문자는 삽입 지점 직전 문자의 서식을 상속.
    private static System.Collections.Generic.List<FlowTextRun> SpliceRuns(System.Collections.Generic.List<FlowTextRun> runs, string oldText, string newText)
    {
        if (oldText == newText)
        {
            return runs;
        }
        if (runs.Count == 0)
        {
            return MakeDefaultRuns(newText);
        }

        int oldLen = oldText.Length, newLen = newText.Length;
        var p = 0;
        while (p < oldLen && p < newLen && oldText[p] == newText[p])
        {
            p++;
        }
        var s = 0;
        while (s < oldLen - p && s < newLen - p && oldText[oldLen - 1 - s] == newText[newLen - 1 - s])
        {
            s++;
        }

        var oldEnd = oldLen - s;
        var ins = newText.Substring(p, newLen - s - p);

        var before = SliceRuns(runs, 0, p);
        var after = SliceRuns(runs, oldEnd, oldLen);

        var combined = new System.Collections.Generic.List<FlowTextRun>();
        combined.AddRange(before);
        if (ins.Length > 0)
        {
            var src = p > 0 ? StyleAt(runs, p - 1) : (runs.Count > 0 ? runs[0] : new FlowTextRun());
            combined.Add(new FlowTextRun { Text = ins, FontFamily = src.FontFamily, Color = src.Color, OutlineColor = src.OutlineColor });
        }
        combined.AddRange(after);
        return CoalesceRuns(combined);
    }

    // [from,to) 구간 런에 set을 적용한다(경계에서 분할 후 적용, 다시 합침).
    private static System.Collections.Generic.List<FlowTextRun> ApplyStyleToRange(System.Collections.Generic.List<FlowTextRun> runs, int from, int to, System.Action<FlowTextRun> set)
    {
        var total = RunsText(runs).Length;
        from = System.Math.Clamp(from, 0, total);
        to = System.Math.Clamp(to, 0, total);
        if (to <= from)
        {
            return runs;
        }
        var before = SliceRuns(runs, 0, from);
        var middle = SliceRuns(runs, from, to);
        var after = SliceRuns(runs, to, total);
        foreach (var r in middle)
        {
            set(r);
        }
        var combined = new System.Collections.Generic.List<FlowTextRun>();
        combined.AddRange(before);
        combined.AddRange(middle);
        combined.AddRange(after);
        return CoalesceRuns(combined);
    }
}
