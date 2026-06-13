using System.Windows;
using System.Windows.Controls;

namespace KomaForge;

public partial class MainWindow : Window
{
    private enum ClipboardKind { None, Panel, Image, Bubble }

    // 내부 클립보드: 마지막으로 복사/잘라낸 대상을 직렬화 DTO로 보관한다.
    private ClipboardKind _clipboardKind = ClipboardKind.None;
    private ComicPanelData? _clipboardPanel;
    private PanelImageData? _clipboardImage;
    private SpeechBubbleData? _clipboardBubble;

    // 붙여넣기 시 원본과 겹치지 않도록 우하단으로 미는 양(px).
    private const double PasteOffset = 24;

    private void Cut_Click(object sender, RoutedEventArgs e) => CutSelection();

    // 클립보드 DTO의 ID를 비워, 붙여넣기로 만들어지는 오브젝트가 (원본과 겹치지 않는) 새 ID를 받게 한다.
    private static void ClearClipboardIds(ComicPanelData panel)
    {
        panel.Id = string.Empty;
        foreach (var image in panel.Images) image.Id = string.Empty;
        foreach (var bubble in panel.Bubbles) bubble.Id = string.Empty;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteClipboard();

    // 선택된 칸/이미지/말풍선을 클립보드로 복사한다(칸은 내부 이미지·말풍선 포함).
    private void CopySelection()
    {
        switch (_selectionKind)
        {
            case SelectionKind.Panel when _selectedPanel != null:
                _clipboardKind = ClipboardKind.Panel;
                _clipboardPanel = CapturePanelData(_selectedPanel);
                ClearClipboardIds(_clipboardPanel);
                UpdateStatus("칸을 복사했습니다.");
                break;
            case SelectionKind.Image when _selectedImage != null:
                _clipboardKind = ClipboardKind.Image;
                _clipboardImage = CaptureImageData(_selectedImage);
                _clipboardImage.Id = string.Empty;
                UpdateStatus("이미지를 복사했습니다.");
                break;
            case SelectionKind.Bubble when _selectedBubble != null:
                _clipboardKind = ClipboardKind.Bubble;
                _clipboardBubble = CaptureBubbleData(_selectedBubble);
                _clipboardBubble.Id = string.Empty;
                UpdateStatus("말풍선을 복사했습니다.");
                break;
            default:
                UpdateStatus("복사할 대상(칸·이미지·말풍선)을 먼저 선택하세요.");
                break;
        }
    }

    // 복사 후 원본을 삭제한다.
    private void CutSelection()
    {
        switch (_selectionKind)
        {
            case SelectionKind.Panel when _selectedPanel != null:
                _clipboardKind = ClipboardKind.Panel;
                _clipboardPanel = CapturePanelData(_selectedPanel);
                ClearClipboardIds(_clipboardPanel);
                DeleteSelectedPanel();
                UpdateStatus("칸을 잘라냈습니다.");
                break;
            case SelectionKind.Image when _selectedImage != null:
                _clipboardKind = ClipboardKind.Image;
                _clipboardImage = CaptureImageData(_selectedImage);
                _clipboardImage.Id = string.Empty;
                DeleteSelectedImage();
                UpdateStatus("이미지를 잘라냈습니다.");
                break;
            case SelectionKind.Bubble when _selectedBubble != null:
                _clipboardKind = ClipboardKind.Bubble;
                _clipboardBubble = CaptureBubbleData(_selectedBubble);
                _clipboardBubble.Id = string.Empty;
                DeleteSelectedBubble();
                UpdateStatus("말풍선을 잘라냈습니다.");
                break;
            default:
                UpdateStatus("잘라낼 대상(칸·이미지·말풍선)을 먼저 선택하세요.");
                break;
        }
    }

    // 선택된 칸을 제거하고 선택 상태를 정리한다(DeletePanel_Click과 공용).
    private void DeleteSelectedPanel()
    {
        if (_selectedPanel == null)
        {
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
    }

    private void PasteClipboard()
    {
        switch (_clipboardKind)
        {
            case ClipboardKind.Panel when _clipboardPanel != null:
                PastePanel(_clipboardPanel);
                break;
            case ClipboardKind.Image when _clipboardImage != null:
                PasteImage(_clipboardImage);
                break;
            case ClipboardKind.Bubble when _clipboardBubble != null:
                PasteBubble(_clipboardBubble);
                break;
            default:
                UpdateStatus("붙여넣을 내용이 없습니다.");
                break;
        }
    }

    // 칸 붙여넣기: 현재 페이지에 같은 위치·크기 칸이 있으면 우하단으로 밀어 넣는다(다른 페이지에도 가능).
    private void PastePanel(ComicPanelData data)
    {
        var x = data.X;
        var y = data.Y;
        while (PanelExistsAt(x, y, data.Width, data.Height))
        {
            x += PasteOffset;
            y += PasteOffset;
        }

        var panel = CreatePanelFromData(data, x, y, _nextPanelNumber++);
        UpdatePanelOrder();
        UpdatePanelList();
        SelectPanel(panel);
        ScrollInspectorToSection();
        UpdateStatus("칸을 붙여넣었습니다.");
    }

    private bool PanelExistsAt(double x, double y, double width, double height)
    {
        const double eps = 0.5;
        foreach (var p in _panels)
        {
            if (Math.Abs(GetCanvasLeft(p.Frame) - x) < eps &&
                Math.Abs(GetCanvasTop(p.Frame) - y) < eps &&
                Math.Abs(p.Frame.Width - width) < eps &&
                Math.Abs(p.Frame.Height - height) < eps)
            {
                return true;
            }
        }

        return false;
    }

    // 이미지 붙여넣기: 대상 칸에 같은 위치·크기 이미지가 있으면 우하단으로 밀어 넣는다(없으면 같은 위치).
    private void PasteImage(PanelImageData data)
    {
        var target = _selectedPanel ?? _panels.LastOrDefault();
        if (target == null)
        {
            UpdateStatus("이미지를 붙여넣을 칸을 먼저 선택하세요.");
            return;
        }

        var tx = data.TranslateX;
        var ty = data.TranslateY;
        while (ImageExistsAt(target, tx, ty, data.Scale))
        {
            tx += PasteOffset;
            ty += PasteOffset;
        }

        var image = AddImageFromData(target, data);
        if (image == null)
        {
            UpdateStatus("원본 이미지 파일을 찾을 수 없어 붙여넣지 못했습니다.");
            return;
        }

        image.Translate.X = tx;
        image.Translate.Y = ty;
        UpdateImageList(target);
        SelectImage(image);
        ScrollInspectorToSection();
        UpdateStatus("이미지를 붙여넣었습니다.");
    }

    private static bool ImageExistsAt(ComicPanel panel, double translateX, double translateY, double scale)
    {
        const double eps = 0.5;
        foreach (var img in panel.Images)
        {
            if (Math.Abs(img.Translate.X - translateX) < eps &&
                Math.Abs(img.Translate.Y - translateY) < eps &&
                Math.Abs(img.Scale.ScaleX - scale) < 0.001)
            {
                return true;
            }
        }

        return false;
    }

    // 말풍선 붙여넣기: 대상 칸에 같은 위치·크기 말풍선이 있으면 우하단으로 밀어 넣는다(없으면 같은 위치).
    private void PasteBubble(SpeechBubbleData data)
    {
        var target = _selectedPanel ?? _panels.LastOrDefault();
        if (target == null)
        {
            UpdateStatus("말풍선을 붙여넣을 칸을 먼저 선택하세요.");
            return;
        }

        var x = data.X;
        var y = data.Y;
        while (BubbleExistsAt(target, x, y, data.Width, data.Height))
        {
            x += PasteOffset;
            y += PasteOffset;
        }

        var bubble = AddBubbleFromData(target, data);
        SetBubblePositionInOwnerPanel(bubble, x, y);
        UpdateBubbleOrder(target);
        UpdateMergedBubbleOutlines(target);
        UpdateBubbleList(target);
        SelectBubble(bubble);
        ScrollInspectorToSection();
        UpdateStatus("말풍선을 붙여넣었습니다.");
    }

    private bool BubbleExistsAt(ComicPanel panel, double x, double y, double width, double height)
    {
        const double eps = 0.5;
        foreach (var b in panel.Bubbles)
        {
            var pos = GetBubblePositionInOwnerPanel(b);
            if (Math.Abs(pos.X - x) < eps &&
                Math.Abs(pos.Y - y) < eps &&
                Math.Abs(b.Container.Width - width) < eps &&
                Math.Abs(b.Container.Height - height) < eps)
            {
                return true;
            }
        }

        return false;
    }
}
