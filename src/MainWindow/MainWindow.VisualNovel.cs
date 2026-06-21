using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace KomaForge;

public partial class MainWindow
{
    // 비주얼 노벨 섹션(스크립트→페이지 생성)의 표시 여부. VN 모드 ON일 때만 보인다.
    private void SetVnSectionVisible(bool on)
    {
        if (VnSectionBorder != null)
        {
            VnSectionBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // 비주얼 노벨 모드에 템플릿이 하나도 없으면 기본 템플릿('이름'·'서술' 말풍선)을 1개 만들어 둔다.
    // VN 모드를 켜는 시점에만 호출한다(실행취소/자동저장 복원 경로에서는 호출하지 않아 지운 템플릿이 되살아나지 않게).
    private void EnsureDefaultVnTemplate()
    {
        if (_vnTemplates.Count > 0)
        {
            return;
        }
        var json = LoadDefaultVnTemplateJson();
        if (json == null)
        {
            return;
        }
        try
        {
            var template = JsonSerializer.Deserialize<ComicPageData>(json);
            if (template == null)
            {
                return;
            }
            RegeneratePageIds(template); // 새 ID 부여(임베디드 원본과 공유 없음).
            template.Name = "템플릿 1";
            _vnTemplates.Add(template);
            _historyDirty = true;
        }
        catch
        {
            // 기본 템플릿 리소스가 손상됐어도 모드 진입은 막지 않는다(빈 목록 유지).
        }
    }

    // 임베디드 리소스에서 기본 템플릿 JSON을 읽는다(없으면 null).
    private static string? LoadDefaultVnTemplateJson()
    {
        var asm = typeof(MainWindow).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("DefaultVnTemplate.json", System.StringComparison.Ordinal));
        if (name == null)
        {
            return null;
        }
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null)
        {
            return null;
        }
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // 템플릿 복제: 템플릿을 편집(선택) 중이면 그 템플릿을, 아니면 현재 일반 페이지를 복제해 템플릿 목록에 추가한다.
    private void VnAddTemplate_Click(object sender, RoutedEventArgs e)
    {
        var fromTemplate = _editingTemplate != null;
        ComicPageData source;
        if (_editingTemplate != null)
        {
            SaveCurrentPageState(); // 편집 내용을 선택 템플릿에 반영한 뒤 복제.
            source = _editingTemplate;
        }
        else
        {
            if (_currentPageIndex < 0 || _currentPageIndex >= _pages.Count)
            {
                UpdateStatus("복제할 페이지가 없습니다.");
                return;
            }
            SaveCurrentPageState();
            source = _pages[_currentPageIndex];
        }

        // JSON 왕복으로 데이터를 깊은 복사하고(원본과 공유 없음), 내부 ID까지 새로 부여해 완전히 독립된 페이지로 만든다.
        var clone = JsonSerializer.Deserialize<ComicPageData>(JsonSerializer.Serialize(source))!;
        RegeneratePageIds(clone);
        clone.Name = $"템플릿 {_vnTemplates.Count + 1}";
        _vnTemplates.Add(clone);
        VnTemplateListBox.SelectedItem = clone; // 선택 시 SelectionChanged가 복제본을 편집 대상으로 연다.
        UpdateStatus(fromTemplate ? "선택한 템플릿을 복제했습니다." : "현재 페이지를 템플릿으로 복제했습니다.");
    }

    private void VnDeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (VnTemplateListBox.SelectedItem is ComicPageData t)
        {
            // 편집 중이던 템플릿을 삭제하면 일반 페이지 편집으로 복귀.
            if (ReferenceEquals(_editingTemplate, t))
            {
                _editingTemplate = null;
                if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
                {
                    LoadPage(_pages[_currentPageIndex]);
                }
            }
            _vnTemplates.Remove(t);
            UpdateStatus("템플릿을 삭제했습니다.");
        }
        else
        {
            UpdateStatus("삭제할 템플릿을 선택하세요.");
        }
    }

    // 템플릿 목록에서 선택하면 그 템플릿을 캔버스로 열어 편집한다(수정은 SaveCurrentPageState로 템플릿에 저장됨).
    private void VnTemplateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSwitchingEditTarget)
        {
            return;
        }
        if (VnTemplateListBox.SelectedItem is not ComicPageData t)
        {
            return;
        }

        SaveCurrentPageState(); // 직전 편집 대상(일반 페이지 또는 다른 템플릿) 저장.
        _editingTemplate = t;
        _isSwitchingEditTarget = true;
        PageListBox.SelectedIndex = -1; // 일반 페이지 선택 해제(편집 대상이 템플릿임을 표시).
        _isSwitchingEditTarget = false;
        LoadPage(t);
        ClearSelection();
        UpdateStatus($"템플릿 '{t.Name}' 편집 중 — 캔버스 수정이 템플릿에 저장됩니다.");
    }

    // 템플릿 편집을 끝내고 일반 페이지 편집으로 복귀한다(템플릿에 최종 반영 후 캔버스를 현재 일반 페이지로).
    private void LeaveTemplateEditing()
    {
        if (_editingTemplate == null)
        {
            return;
        }
        SaveCurrentPageState(); // 템플릿에 최종 반영.
        _editingTemplate = null;
        _isSwitchingEditTarget = true;
        VnTemplateListBox.SelectedIndex = -1;
        _isSwitchingEditTarget = false;
        if (_currentPageIndex >= 0 && _currentPageIndex < _pages.Count)
        {
            LoadPage(_pages[_currentPageIndex]); // 캔버스를 일반 페이지로 복귀.
        }
    }

    // 스크립트 각 줄마다 선택한 템플릿을 복제하고 '이름'/'서술' 말풍선을 교체해 일반 페이지를 만든다.
    private void VnGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (VnTemplateListBox.SelectedItem is not ComicPageData template)
        {
            MessageBox.Show(this, "비주얼 노벨 섹션의 템플릿 페이지를 먼저 선택하세요.",
                "생성", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = (VnScriptBox.Text ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show(this, "스크립트 텍스트를 입력하세요.",
                "생성", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 템플릿에 '이름'·'서술' 말풍선이 모두 있어야 생성한다. 하나라도 없으면 규칙을 안내하고 중지.
        var hasName = TemplateHasBubble(template, "이름");
        var hasNarration = TemplateHasBubble(template, "서술");
        if (!hasName || !hasNarration)
        {
            MessageBox.Show(this,
                "선택한 템플릿 페이지에는 '이름' 말풍선과 '서술' 말풍선이 모두 있어야 합니다.\n" +
                $"(현재: 이름 {(hasName ? "있음" : "없음")}, 서술 {(hasNarration ? "있음" : "없음")})\n\n" +
                "규칙\n" +
                "• 템플릿 페이지에 텍스트가 정확히 '이름'인 말풍선과 '서술'인 말풍선을 각각 만들어 두세요.\n" +
                "• 스크립트 각 줄 '이름: 대사' → '이름' 말풍선=이름, '서술' 말풍선=대사.\n" +
                "• 이름이 없는 줄(서술문)은 '이름' 말풍선을 삭제하고 '서술' 말풍선만 교체합니다.\n" +
                "• 줄마다 템플릿을 복제해 페이지를 만듭니다.",
                "생성 규칙", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveCurrentPageState();
        _historyStructuralPending = true;

        var templateJson = JsonSerializer.Serialize(template);
        foreach (var line in lines)
        {
            SplitScriptLine(line, out var name, out var narration);
            var page = JsonSerializer.Deserialize<ComicPageData>(templateJson)!;
            RegeneratePageIds(page); // 생성된 페이지마다 고유 ID(템플릿·서로와 공유 없음).
            page.Name = $"Page {_pages.Count + 1}";
            ApplyScriptToPage(page, name, narration);
            _pages.Add(page);
        }

        // 생성 후에는 일반 페이지(생성된 첫 페이지)를 편집하도록 템플릿 편집 모드를 해제한다.
        _editingTemplate = null;
        _isSwitchingEditTarget = true;
        VnTemplateListBox.SelectedIndex = -1;
        _isSwitchingEditTarget = false;
        _currentPageIndex = _pages.Count - lines.Count;
        LoadPage(_pages[_currentPageIndex]);
        ClearSelection();
        UpdatePageList();
        UpdateStatus($"스크립트 {lines.Count}줄로 {lines.Count}개 페이지를 생성했습니다.");
    }

    // 복제 페이지의 칸·이미지·말풍선 ID를 모두 새로 부여한다(원본과 동일 식별자를 쓰지 않게 — 완전히 새 페이지).
    private static void RegeneratePageIds(ComicPageData page)
    {
        foreach (var panel in page.Panels)
        {
            panel.Id = System.Guid.NewGuid().ToString("N");
            foreach (var image in panel.Images)
            {
                image.Id = System.Guid.NewGuid().ToString("N");
            }
            foreach (var bubble in panel.Bubbles)
            {
                bubble.Id = System.Guid.NewGuid().ToString("N");
            }
        }
    }

    private static bool TemplateHasBubble(ComicPageData page, string marker)
        => page.Panels.Any(p => p.Bubbles.Any(b => (b.Text ?? string.Empty).Trim() == marker));

    // '이름: 대사' → name/narration. 콜론(':' 또는 '：')이 없으면 name=null, narration=전체 줄.
    private static void SplitScriptLine(string line, out string? name, out string narration)
    {
        var idx = line.IndexOfAny(new[] { ':', '：' });
        if (idx > 0)
        {
            name = line[..idx].Trim();
            narration = line[(idx + 1)..].Trim();
            if (name.Length == 0)
            {
                name = null;
            }
        }
        else
        {
            name = null;
            narration = line.Trim();
        }
    }

    // 복제 페이지의 '이름'/'서술' 말풍선을 교체한다. 이름이 없는 줄이면 '이름' 말풍선은 삭제.
    private static void ApplyScriptToPage(ComicPageData page, string? name, string narration)
    {
        foreach (var panel in page.Panels)
        {
            var kept = new List<SpeechBubbleData>();
            foreach (var b in panel.Bubbles)
            {
                var marker = (b.Text ?? string.Empty).Trim();
                if (marker == "이름")
                {
                    if (name == null)
                    {
                        continue; // 이름 없는 줄 → 이름 말풍선 삭제.
                    }
                    SetBubbleScriptText(b, name);
                }
                else if (marker == "서술")
                {
                    SetBubbleScriptText(b, narration);
                }
                kept.Add(b);
            }
            panel.Bubbles = kept;
        }
    }

    private static void SetBubbleScriptText(SpeechBubbleData b, string text)
    {
        b.Text = text;
        b.Runs = new List<FlowTextRun>(); // 구간 서식 초기화(불러올 때 단일 런으로 보임).
    }
}
