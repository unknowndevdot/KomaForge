using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KomaForge;

public partial class MainWindow : Window
{
    // 단축키 조합(수정자 + 키). Key.None이면 '없음'(미할당).
    private readonly record struct Shortcut(ModifierKeys Mods, Key Key);

    // 편집 가능한 명령 단축키 정의(액션 id, 표시명, 기본 수정자, 기본 키).
    private static readonly (string Id, string Label, ModifierKeys Mods, Key Key)[] ShortcutDefs =
    {
        ("new",   "새로 만들기",     ModifierKeys.Control, Key.N),
        ("open",  "불러오기",       ModifierKeys.Control, Key.O),
        ("save",  "저장",           ModifierKeys.Control, Key.S),
        ("undo",  "실행취소",       ModifierKeys.Control, Key.Z),
        ("redo",  "다시실행",       ModifierKeys.Control, Key.Y),
        ("cut",   "잘라내기",       ModifierKeys.Control, Key.X),
        ("copy",  "복사",           ModifierKeys.Control, Key.C),
        ("paste", "붙여넣기",       ModifierKeys.Control, Key.V),
        ("reset", "선택 리셋",      ModifierKeys.Control, Key.R),
        ("lock",  "선택 잠금/해제", ModifierKeys.None,    Key.L),
        ("toggletext", "비주얼 노벨 모드 (테스트)", ModifierKeys.Control, Key.T),
        ("preferences", "환경설정", ModifierKeys.Control, Key.OemComma),
    };

    // 기호 키의 보기 좋은 표기(저장/파싱 양방향에 사용).
    private static readonly (Key Key, string Text)[] KeySymbolMap =
    {
        (Key.OemComma, ","),
        (Key.OemPeriod, "."),
        (Key.OemQuestion, "/"),
        (Key.OemMinus, "-"),
        (Key.OemPlus, "="),
        (Key.OemSemicolon, ";"),
        (Key.Space, "Space"),
    };

    private readonly Dictionary<string, Shortcut> _shortcuts = new();

    private void InitShortcuts()
    {
        foreach (var d in ShortcutDefs)
        {
            _shortcuts[d.Id] = new Shortcut(d.Mods, d.Key);
        }
    }

    // 눌린 키 조합이 등록된 단축키와 맞으면 해당 동작을 실행한다.
    private bool TryRunCustomShortcut(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            return false;
        }

        var mods = Keyboard.Modifiers;
        foreach (var (id, sc) in _shortcuts)
        {
            if (sc.Key == Key.None || sc.Key != key || sc.Mods != mods)
            {
                continue;
            }

            // 텍스트 입력 중에는 텍스트 편집과 충돌하는 단축키(수정자 없음, Ctrl+C/V/X/Z/Y/A)는 양보한다.
            if (Keyboard.FocusedElement is TextBox && ConflictsWithTextEditing(sc))
            {
                return false;
            }

            RunShortcutAction(id);
            return true;
        }

        return false;
    }

    private void RunShortcutAction(string id)
    {
        switch (id)
        {
            case "new": NewProject_Click(this, new RoutedEventArgs()); break;
            case "open": LoadProject_Click(this, new RoutedEventArgs()); break;
            case "save": SaveProjectToCurrentOrPrompt(); break;
            case "undo": Undo(); break;
            case "redo": Redo(); break;
            case "cut": CutSelection(); break;
            case "copy": CopySelection(); break;
            case "paste": PasteClipboard(); break;
            case "reset": ResetSelectedToDefault(); break;
            case "lock": ToggleSelectedLock(); break;
            case "toggletext": ToggleTextMode(); break;
            case "preferences": ShowPreferencesDialog(); break;
        }
    }

    private static bool ConflictsWithTextEditing(Shortcut sc)
    {
        if (sc.Mods == ModifierKeys.None)
        {
            return true;
        }

        return sc.Mods == ModifierKeys.Control &&
               sc.Key is Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.A;
    }

    private static bool IsModifierKey(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
          or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None;

    // --- 직렬화/표기 ---

    private static string FormatGesture(Shortcut sc)
    {
        if (sc.Key == Key.None)
        {
            return "(없음)";
        }

        var parts = new List<string>();
        if (sc.Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (sc.Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (sc.Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyToText(sc.Key));
        return string.Join("+", parts);
    }

    private static string KeyToText(Key key)
    {
        foreach (var (k, text) in KeySymbolMap)
        {
            if (k == key) return text;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "Num" + (int)(key - Key.NumPad0);
        }

        return key.ToString();
    }

    private static bool TryParseGesture(string? text, out Shortcut sc)
    {
        sc = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var mods = ModifierKeys.None;
        var key = Key.None;
        var keySet = false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                default:
                    if (!TextToKey(raw, out key)) return false;
                    keySet = true;
                    break;
            }
        }

        if (!keySet)
        {
            return false;
        }

        sc = new Shortcut(mods, key);
        return true;
    }

    private static bool TextToKey(string token, out Key key)
    {
        foreach (var (k, text) in KeySymbolMap)
        {
            if (string.Equals(token, text, StringComparison.OrdinalIgnoreCase))
            {
                key = k;
                return true;
            }
        }

        if (token.Length == 1 && token[0] is >= '0' and <= '9')
        {
            key = Key.D0 + (token[0] - '0');
            return true;
        }

        if (token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
            token.Length == 4 && token[3] is >= '0' and <= '9')
        {
            key = Key.NumPad0 + (token[3] - '0');
            return true;
        }

        return Enum.TryParse(token, true, out key) && key != Key.None;
    }

    private Dictionary<string, string> ExportShortcuts()
    {
        var map = new Dictionary<string, string>();
        foreach (var (id, sc) in _shortcuts)
        {
            map[id] = sc.Key == Key.None ? "" : FormatGesture(sc);
        }

        return map;
    }

    private void ImportShortcuts(Dictionary<string, string>? saved)
    {
        if (saved == null)
        {
            return;
        }

        foreach (var def in ShortcutDefs)
        {
            if (saved.TryGetValue(def.Id, out var text))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    _shortcuts[def.Id] = new Shortcut(ModifierKeys.None, Key.None);
                }
                else if (TryParseGesture(text, out var sc))
                {
                    _shortcuts[def.Id] = sc;
                }
            }
        }
    }

    // 메뉴 항목의 단축키 표기를 현재 설정으로 맞춘다.
    private void RefreshShortcutMenuText()
    {
        NewMenuItem.InputGestureText = GestureText("new");
        OpenMenuItem.InputGestureText = GestureText("open");
        SaveMenuItem.InputGestureText = GestureText("save");
        UndoMenuItem.InputGestureText = GestureText("undo");
        RedoMenuItem.InputGestureText = GestureText("redo");
        CutMenuItem.InputGestureText = GestureText("cut");
        CopyMenuItem.InputGestureText = GestureText("copy");
        PasteMenuItem.InputGestureText = GestureText("paste");
        PreferencesMenuItem.InputGestureText = GestureText("preferences");
    }

    private string GestureText(string id) =>
        _shortcuts.TryGetValue(id, out var sc) && sc.Key != Key.None ? FormatGesture(sc) : string.Empty;

    // --- 환경설정(단축키 편집) 대화상자 ---

    private void Preferences_Click(object sender, RoutedEventArgs e) => ShowPreferencesDialog();

    private void ShowPreferencesDialog()
    {
        var working = new Dictionary<string, Shortcut>(_shortcuts);
        var buttons = new Dictionary<string, Button>();
        string? capturing = null;

        var dialog = new Window
        {
            Title = "환경설정",
            Width = 760,
            Height = 600,
            MinWidth = 520,
            MinHeight = 400,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        void RefreshButtons()
        {
            foreach (var (id, btn) in buttons)
            {
                btn.Content = capturing == id ? "[ 키 입력… ]" : FormatGesture(working[id]);
            }
        }

        // --- '단축키' 탭 내용 ---
        var shortcutPanel = new StackPanel { Margin = new Thickness(16) };
        shortcutPanel.Children.Add(new TextBlock
        {
            Text = "변경할 항목을 클릭한 뒤 새 키 조합을 누르세요. (Esc=취소, Delete/Backspace=없음)",
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x51, 0x4A)),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var def in ShortcutDefs)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
            row.Children.Add(new TextBlock
            {
                Text = def.Label,
                Width = 150,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
            });

            var btn = new Button
            {
                MinWidth = 150,
                MinHeight = 24,
                Padding = new Thickness(10, 2, 10, 2),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, // DockPanel 세로 stretch 방지(인스펙터와 같은 높이)
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            var id = def.Id;
            btn.Click += (_, _) => { capturing = id; RefreshButtons(); };
            DockPanel.SetDock(btn, Dock.Right);
            buttons[id] = btn;
            row.Children.Add(btn);
            shortcutPanel.Children.Add(row);
        }

        var restoreBtn = new Button
        {
            Content = "기본값 복원",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        restoreBtn.Click += (_, _) =>
        {
            foreach (var def in ShortcutDefs)
            {
                working[def.Id] = new Shortcut(def.Mods, def.Key);
            }
            capturing = null;
            RefreshButtons();
        };
        shortcutPanel.Children.Add(restoreBtn);

        // --- 왼쪽 탭(향후 항목 추가 예정) ---
        // 콘텐츠 영역은 흰 배경+테두리로, 왼쪽 탭 영역과 시각적으로 구분한다.
        var tabs = new TabControl
        {
            TabStripPlacement = Dock.Left,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0xD3, 0xC6)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Margin = new Thickness(12, 12, 12, 0)
        };
        // --- '일반' 탭 내용 ---
        var workingSelectionPreview = _selectionPreviewEnabled;
        var generalPanel = new StackPanel { Margin = new Thickness(16) };

        var previewCheck = new CheckBox
        {
            Content = "선택 미리보기 강조",
            IsChecked = workingSelectionPreview,
            Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
        };
        previewCheck.Checked += (_, _) => workingSelectionPreview = true;
        previewCheck.Unchecked += (_, _) => workingSelectionPreview = false;
        generalPanel.Children.Add(previewCheck);

        generalPanel.Children.Add(new TextBlock
        {
            Text = "마우스를 올린 곳에서 클릭하면 선택될 오브젝트를 미리 색으로 표시합니다.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x72, 0x68)),
            Margin = new Thickness(24, 4, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        var workingKeepAspect = _keepAspectRatio;
        var keepAspectCheck = new CheckBox
        {
            Content = "이미지 크기 조절 시 비율 유지",
            IsChecked = workingKeepAspect,
            Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
        };
        keepAspectCheck.Checked += (_, _) => workingKeepAspect = true;
        keepAspectCheck.Unchecked += (_, _) => workingKeepAspect = false;
        generalPanel.Children.Add(keepAspectCheck);

        generalPanel.Children.Add(new TextBlock
        {
            Text = "켜면 이미지 크기를 항상 비율 유지한 채 조절합니다. 끄면 자유롭게 조절하되 Shift를 누르면 비율이 유지됩니다. (칸·말풍선은 Shift를 누를 때만 비율 유지)",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x72, 0x68)),
            Margin = new Thickness(24, 4, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        var workingAutosaveDisabled = _autosaveDisabled;
        var autosaveOffCheck = new CheckBox
        {
            Content = "자동저장 끄기",
            IsChecked = workingAutosaveDisabled,
            Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
        };
        autosaveOffCheck.Checked += (_, _) => workingAutosaveDisabled = true;
        autosaveOffCheck.Unchecked += (_, _) => workingAutosaveDisabled = false;
        generalPanel.Children.Add(autosaveOffCheck);

        generalPanel.Children.Add(new TextBlock
        {
            Text = "켜면 편집 중 주기적으로 일어나는 자동저장을 멈춥니다. 페이지가 매우 많아 자동저장이 렉을 유발할 때 사용하세요. (실행 취소/다시 실행은 그대로 동작하며, 정상 종료 시에는 한 번 저장됩니다.)",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x72, 0x68)),
            Margin = new Thickness(24, 4, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        var workingImageCacheMb = _imageCacheLimitMb;
        var cacheRow = new DockPanel { Margin = new Thickness(0, 0, 0, 0), LastChildFill = false };
        cacheRow.Children.Add(new TextBlock
        {
            Text = "이미지 디코드 캐시 한도(MB)",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24))
        });
        var cacheBox = new TextBox
        {
            Text = workingImageCacheMb.ToString(),
            Width = 80,
            MinHeight = 24,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        cacheBox.TextChanged += (_, _) =>
        {
            // 숫자만 허용. 비었거나 음수면 0(=끔)으로 본다. 상한은 적용 시 클램프.
            workingImageCacheMb = int.TryParse(cacheBox.Text, out var v) && v > 0 ? v : 0;
        };
        DockPanel.SetDock(cacheBox, Dock.Right);
        cacheRow.Children.Add(cacheBox);
        generalPanel.Children.Add(cacheRow);

        generalPanel.Children.Add(new TextBlock
        {
            Text = "같은 이미지 파일을 다시 디코드하지 않고 재사용해 페이지 전환을 빠르게 합니다. 값이 클수록 더 많은 이미지를 기억하지만 메모리를 더 씁니다. 0이면 캐시를 끕니다. (기본 256MB)",
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x72, 0x68)),
            Margin = new Thickness(24, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        tabs.Items.Add(new TabItem
        {
            Header = "일반",
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = generalPanel
            }
        });
        tabs.Items.Add(new TabItem
        {
            Header = "단축키",
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = shortcutPanel
            }
        });

        // --- 하단 확인/취소(모든 탭 공통) ---
        var cancelBtn = new Button { Content = "취소", MinWidth = 72 };
        cancelBtn.Click += (_, _) => dialog.Close();
        var okBtn = new Button { Content = "확인", MinWidth = 72 };
        okBtn.Click += (_, _) =>
        {
            _shortcuts.Clear();
            foreach (var (id, sc) in working)
            {
                _shortcuts[id] = sc;
            }
            _selectionPreviewEnabled = workingSelectionPreview;
            if (!_selectionPreviewEnabled)
            {
                ClearHoverHighlight(); // 끄면 현재 떠 있는 강조도 즉시 제거.
            }
            _keepAspectRatio = workingKeepAspect;
            _autosaveDisabled = workingAutosaveDisabled;
            if (_autosaveDisabled)
            {
                _autosavePending = false; // 끄면 대기 중인 자동저장도 즉시 취소(렉 유발 쓰기 방지).
            }
            ApplyImageCacheLimit(Math.Clamp(workingImageCacheMb, 0, 8192));
            RefreshShortcutMenuText();
            SaveWindowSettings();
            dialog.DialogResult = true;
        };
        var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 12), LastChildFill = false };
        DockPanel.SetDock(cancelBtn, Dock.Right);
        DockPanel.SetDock(okBtn, Dock.Right);
        bottom.Children.Add(cancelBtn);
        bottom.Children.Add(okBtn);

        var rootDock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        rootDock.Children.Add(bottom);
        rootDock.Children.Add(tabs);

        dialog.Content = rootDock;

        dialog.PreviewKeyDown += (_, e) =>
        {
            if (capturing == null)
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsModifierKey(key))
            {
                e.Handled = true;
                return; // 수정자만 눌린 상태: 실제 키를 기다린다.
            }

            e.Handled = true;
            if (key == Key.Escape)
            {
                capturing = null;
                RefreshButtons();
                return;
            }

            // Delete/Backspace = 단축키 없음으로.
            if (key is Key.Delete or Key.Back)
            {
                working[capturing] = new Shortcut(ModifierKeys.None, Key.None);
                capturing = null;
                RefreshButtons();
                return;
            }

            var sc = new Shortcut(Keyboard.Modifiers, key);
            // 같은 조합을 가진 다른 액션은 '없음'으로 비워 중복을 막는다(키를 빼앗아 옴).
            foreach (var otherId in working.Keys.ToList())
            {
                if (otherId != capturing && working[otherId].Equals(sc))
                {
                    working[otherId] = new Shortcut(ModifierKeys.None, Key.None);
                }
            }

            working[capturing] = sc;
            capturing = null;
            RefreshButtons();
        };

        RefreshButtons();
        dialog.ShowDialog();
    }
}
