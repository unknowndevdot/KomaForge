using System.Windows;

namespace KomaForge;

public partial class MainWindow : Window
{
    // 비주얼 노벨 모드 상태. Enabled가 핵심이며(모드 ON/OFF), 나머지 옛 흐름텍스트 필드는 더 이상 UI가 없다.
    private FlowTextData _flow = new();

    // 저장/복원에서 받은 데이터로 모드 상태를 적용한다(숨은 체크박스·VN 섹션 표시 동기화).
    private void ApplyFlowText(FlowTextData? data)
    {
        _flow = data?.Clone() ?? new FlowTextData();
        _isLoadingInspector = true;
        FlowTextModeCheckBox.IsChecked = _flow.Enabled;
        SetVnSectionVisible(_flow.Enabled);
        _isLoadingInspector = false;
    }

    // 비주얼 노벨 모드 ON/OFF 토글(Ctrl+T). 숨겨진 체크박스로 VN 섹션·페이지 목록 라벨을 함께 갱신.
    private void ToggleTextMode()
    {
        if (FlowTextModeCheckBox == null)
        {
            return;
        }
        FlowTextModeCheckBox.IsChecked = !(FlowTextModeCheckBox.IsChecked == true);
        UpdateStatus(FlowTextModeCheckBox.IsChecked == true ? "비주얼 노벨 모드 ON" : "비주얼 노벨 모드 OFF");
    }

    private void FlowTextModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SetVnSectionVisible(FlowTextModeCheckBox.IsChecked == true);

        if (!_flowReady || _isLoadingInspector)
        {
            return;
        }

        _flow.Enabled = FlowTextModeCheckBox.IsChecked == true;
        _historyDirty = true;
        if (_flow.Enabled)
        {
            EnsureDefaultVnTemplate(); // 템플릿이 하나도 없으면 기본 템플릿을 만들어 둔다.
        }
        else
        {
            LeaveTemplateEditing(); // VN 모드를 끄면 템플릿 편집을 종료하고 일반 페이지로 복귀.
        }

        // 페이지 목록 라벨을 모드에 맞게 전환(현재 페이지 말풍선을 먼저 반영).
        SaveCurrentPageState();
        foreach (var p in _pages)
        {
            p.VisualNovelMode = _flow.Enabled;
        }
    }

    // 비주얼 노벨 모드에서 현재 페이지 말풍선 변경 시 목록 요약을 즉시 갱신한다.
    private void RefreshCurrentPageLabel()
    {
        if (!_flow.Enabled || _currentPageIndex < 0 || _currentPageIndex >= _pages.Count)
        {
            return;
        }
        SaveCurrentPageState();
        _pages[_currentPageIndex].RefreshDisplayLabel();
    }

    // 가로 정렬 문자열 → TextAlignment(말풍선 텍스트 정렬·구간 직렬화 복원에서 사용).
    private static TextAlignment ParseFlowAlignment(string a) => a switch
    {
        "Center" => TextAlignment.Center,
        "Right" => TextAlignment.Right,
        "Justify" => TextAlignment.Justify,
        _ => TextAlignment.Left
    };

    // --- 구간(런) 스팬 유틸 (말풍선 구간별 서식에서 재사용) ---

    private static System.Collections.Generic.List<FlowTextRun> MakeDefaultRuns(string text)
        => string.IsNullOrEmpty(text)
            ? new System.Collections.Generic.List<FlowTextRun>()
            : new System.Collections.Generic.List<FlowTextRun> { new FlowTextRun { Text = text } };

    private static string RunsText(System.Collections.Generic.List<FlowTextRun> runs)
        => string.Concat(System.Linq.Enumerable.Select(runs, r => r.Text));

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
