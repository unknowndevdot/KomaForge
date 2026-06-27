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
    private void UpdatePanelImageSizes(ComicPanel panel)
    {
        // 이미지는 추가 시점의 크기·위치를 그대로 유지한다(말풍선처럼 칸 리사이즈에 영향받지 않게).
        // 따라서 칸 크기가 바뀌어도 이미지 Content/Layer는 다시 맞추지 않고, 칸 모양/클립만 갱신한다.
        // (잘려 보이는 범위만 새 칸 사변형을 따른다.)
        UpdatePanelShape(panel);
    }

    private void UpdateBubbleGeometry(SpeechBubble bubble)
    {
        var width = Math.Max(1, bubble.Container.Width);
        var height = Math.Max(1, bubble.Container.Height);

        bubble.BodyPath.Data = bubble.Shape switch
        {
            BubbleShape.CloudExplosion => CreateCloudExplosionGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength, bubble.ShapeIrregularity, bubble.ShapeWidthVariation),
            BubbleShape.Flash => CreateFlashGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength, bubble.ShapeIrregularity),
            BubbleShape.ConcentrationLines => CreateConcentrationLinesGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength, bubble.ShapeIrregularity),
            BubbleShape.EffectLines => CreateEffectLinesGeometry(width, height, bubble.ShapeCount, bubble.ShapeStrength, bubble.ShapeIrregularity),
            // 테두리 없음: 본체 도형 없이 글자만 보인다(채움·외곽선 모두 없음).
            BubbleShape.None => null,
            _ => CreateRoundRectGeometry(width, height, bubble.ShapeStrength)
        };

        // 말풍선 크기에 맞춰 글자 크기를 자동 축소(설정값을 최대로).
        ApplyBubbleAutoFit(bubble);

        UpdateBubbleTailHandles(bubble);
        ApplyBubbleTextWarp(bubble); // 글자 모서리 조절 파라미터 갱신(크기·변위 변화 반영).
        // 이 말풍선이 속한 칸만 갱신해도 충분하다(다른 칸의 도형은 영향받지 않음).
        UpdateMergedBubbleOutlines(bubble.OwnerPanel);
    }

    // 글자 워프 파라미터(컨테이너 크기·모서리 변위)를 글자 요소에 전달한다. OFF/변위 0이면 워프 해제.
    private static void ApplyBubbleTextWarp(SpeechBubble bubble)
    {
        var tb = bubble.TextBlock;
        if (bubble.WarpText && HasCornerWarp(bubble.CornerOffsets))
        {
            tb.WarpContainerSize = new Size(Math.Max(1, bubble.Container.Width), Math.Max(1, bubble.Container.Height));
            // 새 배열로 전달해 DP 변경(재렌더)이 감지되게 한다.
            tb.WarpOffsets = new[]
            {
                bubble.CornerOffsets[0], bubble.CornerOffsets[1], bubble.CornerOffsets[2], bubble.CornerOffsets[3]
            };
        }
        else
        {
            tb.WarpOffsets = null;
        }

        // 글자 회전('텍스트 회전' 값). 선효과(속도선·집중선)는 대사가 없으니 제외, 그 외 모든 말풍선은 글자 요소 중심 기준으로 돌린다.
        if (!IsLineEffectShape(bubble.Shape) && Math.Abs(bubble.TextRotation) > 0.01)
        {
            tb.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            tb.RenderTransform = new System.Windows.Media.RotateTransform(bubble.TextRotation);
        }
        else
        {
            tb.RenderTransform = null;
        }
    }

    // 말풍선 안에 글자가 들어가도록, 설정 글자 크기(MaxFontSize)를 최대로 두고
    // 들어가지 않으면 실제 렌더 크기(TextBlock.FontSize)를 줄인다(이분 탐색).
    private static void ApplyBubbleAutoFit(SpeechBubble bubble)
    {
        var tb = bubble.TextBlock;
        var max = bubble.MaxFontSize;
        const double min = 6;

        var m = tb.Margin;
        var p = tb.Padding;
        var availW = bubble.Container.Width - m.Left - m.Right - p.Left - p.Right;
        var availH = bubble.Container.Height - m.Top - m.Bottom - p.Top - p.Bottom;

        if (availW <= 1 || availH <= 1 || string.IsNullOrEmpty(tb.Text))
        {
            tb.FontSize = Math.Max(min, max);
            return;
        }

        bool Fits(double f)
        {
            var s = tb.MeasureAtFont(f, availW);
            return s.Width <= availW + 0.5 && s.Height <= availH + 0.5;
        }

        if (Fits(max))
        {
            tb.FontSize = max;
            return;
        }

        double lo = min, hi = max;
        for (var i = 0; i < 14; i++)
        {
            var mid = (lo + hi) / 2;
            if (Fits(mid)) lo = mid; else hi = mid;
        }

        tb.FontSize = Math.Max(min, lo);
    }

    // 채움은 말풍선별 ShapePath가(배경색을 따로 줄 수 있게), 외곽선은 칸 단위로 합쳐(Union) 그린다.
    // 이렇게 하면 겹친 말풍선들의 경계선이 하나로 이어져 도형이 합쳐진 것처럼 보인다.
    // only가 지정되면 그 칸만 갱신한다(드래그/슬라이더 등 한 칸만 바뀌는 경우의 비용 절감).
    private void UpdateMergedBubbleOutlines(ComicPanel? only = null)
    {
        foreach (var panel in _panels)
        {
            if (only != null && !ReferenceEquals(panel, only))
            {
                continue;
            }

            // 채움은 말풍선별 ShapePath가 담당하므로 칸 단위 채움 경로는 비운다.
            panel.BubbleFillPath.Data = null;
            panel.FreeBubbleFillPath.Data = null;

            foreach (var bubble in panel.Bubbles)
            {
                UpdateBubbleShapePath(bubble);
            }

            // 외곽선은 테두리색별로 합쳐 그린다(같은 색 겹침은 하나로 연결, 다른 색은 따로).
            // 기존 단일 경로는 쓰지 않고(아래), 색 그룹마다 동적 경로를 만든다.
            panel.BubbleOutlinePath.Data = null;
            panel.FreeBubbleOutlinePath.Data = null;
            foreach (var p in panel.DynamicBubbleOutlines)
            {
                (p.Parent as Canvas)?.Children.Remove(p);
            }
            panel.DynamicBubbleOutlines.Clear();
            AddBubbleOutlineGroups(panel, panel.Overlay, cropped: true);
            AddBubbleOutlineGroups(panel, panel.FreeOverlay, cropped: false);
        }
    }

    // 한 크롭 그룹의 말풍선들을 테두리색별로 Union해, 색마다 외곽선 경로 하나를 만들어 오버레이에 추가한다.
    private void AddBubbleOutlineGroups(ComicPanel panel, Canvas host, bool cropped)
    {
        var map = new Dictionary<Color, Geometry>();
        var order = new List<Color>();
        foreach (var bubble in panel.Bubbles)
        {
            if (bubble.IsCropped != cropped)
            {
                continue;
            }

            var geometry = BuildBubbleOverlayGeometry(bubble);
            if (geometry == null)
            {
                continue;
            }

            if (map.TryGetValue(bubble.BorderColor, out var existing))
            {
                map[bubble.BorderColor] = Geometry.Combine(existing, geometry, GeometryCombineMode.Union, null);
            }
            else
            {
                map[bubble.BorderColor] = geometry;
                order.Add(bubble.BorderColor);
            }
        }

        foreach (var color in order)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = map[color],
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(path, int.MaxValue - 1);
            host.Children.Add(path);
            panel.DynamicBubbleOutlines.Add(path);
        }
    }

    // 한 말풍선의 본체+꼬리 도형을 오버레이 좌표로 만들어 ShapePath에 적용하고 배경색을 입힌다.
    private static void UpdateBubbleShapePath(SpeechBubble bubble)
    {
        // 선 효과는 대사를 숨기고, 일반 말풍선은 보인다.
        bubble.TextBlock.Visibility = IsLineEffectShape(bubble.Shape) ? Visibility.Collapsed : Visibility.Visible;
        // 선 효과가 아닐 때는 선 호스트를 비운다.
        if (!IsLineEffectShape(bubble.Shape))
        {
            bubble.LineHost.Children.Clear();
            bubble.LineHostSignature = null;
        }

        if (bubble.BodyPath.Data == null)
        {
            // 테두리 없음: 도형 자체가 없다.
            bubble.ShapePath.Data = null;
            bubble.ShapePath.Clip = null;
            return;
        }

        // 선 효과(집중선/속도선): 선마다 개별 Path로(컨테이너 안 선 호스트) 그려 각 선이 자기 시작점부터 페이드되게 한다.
        // 선 호스트는 컨테이너 로컬 좌표라 위치 변경 시 자동으로 따라온다 → 크기/모양/돌기/강도/색이 바뀔 때만 재생성한다.
        if (IsLineEffectShape(bubble.Shape))
        {
            bubble.ShapePath.Data = null;
            bubble.ShapePath.Clip = null;

            // 모서리 조절(도형)이 켜지면 변위가 바뀔 때마다 선을 다시 그려야 하므로 시그니처에 포함한다.
            var warpSig = bubble.WarpShape
                ? string.Join(",", bubble.CornerOffsets.Select(p => $"{p.X:F1}:{p.Y:F1}"))
                : "off";
            var signature = $"{bubble.Shape}|{bubble.Container.Width:F1}|{bubble.Container.Height:F1}|{bubble.ShapeCount}|{bubble.ShapeStrength:F1}|{bubble.ShapeIrregularity:F1}|{ToHex(bubble.TextBlock.Fill)}|{warpSig}|{bubble.LineFadeBothSides}";
            if (signature == bubble.LineHostSignature)
            {
                return; // 위치만 바뀐 경우: 로컬 좌표라 그대로 따라오므로 재생성 불필요.
            }

            bubble.LineHostSignature = signature;
            if (bubble.Shape == BubbleShape.ConcentrationLines)
            {
                BuildConcentrationLineHost(bubble);
            }
            else
            {
                BuildEffectLineHost(bubble);
            }

            return;
        }

        // 일반 말풍선: 본체+꼬리를 합친 도형에 배경색 채움만 칠한다.
        // 외곽선(테두리)은 칸 단위 병합 경로(BubbleOutlinePath)가 그려, 겹친 말풍선의 경계선이 하나로 이어진다.
        bubble.ShapePath.Data = BuildBubbleOverlayGeometry(bubble);
        bubble.ShapePath.Fill = bubble.BackgroundBrush;
        bubble.ShapePath.Stroke = Brushes.Transparent;
        bubble.ShapePath.StrokeThickness = 0;
        bubble.ShapePath.Clip = null;
    }

    // 말풍선 본체+꼬리(안으로 깎기 포함)를 오버레이 좌표의 한 도형으로 만든다.
    // 선 효과/테두리 없음은 본체 도형이 없으므로 null.
    private static Geometry? BuildBubbleOverlayGeometry(SpeechBubble bubble)
    {
        if (bubble.BodyPath.Data == null || IsLineEffectShape(bubble.Shape))
        {
            return null;
        }

        // 먼저 본체+꼬리를 로컬 좌표(컨테이너 원점 기준)에서 합친다.
        Geometry shape = bubble.BodyPath.Data.Clone();
        foreach (var tail in bubble.Tails)
        {
            var tailGeometry = CreateTailGeometry(tail);
            // 생각 꼬리(원 3개)는 항상 합친다(안으로 깎기는 곡선 꼬리에만 의미).
            var tailMode = (tail.TailInward && !tail.ThoughtTail) ? GeometryCombineMode.Exclude : GeometryCombineMode.Union;
            shape = Geometry.Combine(shape, tailGeometry, tailMode, null);
        }

        // 모서리 조절(도형)이 켜져 있고 변위가 있으면 사변형으로 일그러뜨린다.
        if (bubble.WarpShape && HasCornerWarp(bubble.CornerOffsets))
        {
            shape = WarpGeometry(shape, Math.Max(1, bubble.Container.Width), Math.Max(1, bubble.Container.Height), bubble.CornerOffsets);
        }

        // 마지막에 오버레이 좌표로 평행이동.
        shape.Transform = new TranslateTransform(GetCanvasLeft(bubble.Container), GetCanvasTop(bubble.Container));
        return shape;
    }

    // --- 모서리 조절(사변형 워프) 공용 ---

    private static bool HasCornerWarp(Point[] o)
        => o[0].X != 0 || o[0].Y != 0 || o[1].X != 0 || o[1].Y != 0
           || o[2].X != 0 || o[2].Y != 0 || o[3].X != 0 || o[3].Y != 0;

    // 로컬 좌표(0..w, 0..h)의 점을 네 모서리(오프셋 반영)가 만드는 사변형으로 이중선형 매핑한다.
    // 변위가 모두 0이면 항등(원래 좌표 그대로).
    internal static Point WarpPoint(double x, double y, double w, double h, Point[] o)
    {
        var u = w > 0 ? x / w : 0;
        var v = h > 0 ? y / h : 0;
        var tlX = o[0].X;          var tlY = o[0].Y;
        var trX = w + o[1].X;      var trY = o[1].Y;
        var brX = w + o[2].X;      var brY = h + o[2].Y;
        var blX = o[3].X;          var blY = h + o[3].Y;
        var nx = (1 - u) * (1 - v) * tlX + u * (1 - v) * trX + u * v * brX + (1 - u) * v * blX;
        var ny = (1 - u) * (1 - v) * tlY + u * (1 - v) * trY + u * v * brY + (1 - u) * v * blY;
        return new Point(nx, ny);
    }

    // 곡선을 포함한 도형을 잘게 직선화한 뒤 각 점을 워프해 새 도형으로 만든다(비아핀 변환이라 직접 Transform 불가).
    private static Geometry WarpGeometry(Geometry geo, double w, double h, Point[] o)
    {
        var flat = geo.GetFlattenedPathGeometry(0.2, ToleranceType.Absolute);
        var result = new PathGeometry { FillRule = flat.FillRule };
        foreach (var fig in flat.Figures)
        {
            var nf = new PathFigure
            {
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled,
                StartPoint = WarpPoint(fig.StartPoint.X, fig.StartPoint.Y, w, h, o)
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pls)
                {
                    var pts = new PointCollection();
                    foreach (var p in pls.Points)
                    {
                        pts.Add(WarpPoint(p.X, p.Y, w, h, o));
                    }
                    nf.Segments.Add(new PolyLineSegment(pts, seg.IsStroked));
                }
                else if (seg is LineSegment lseg)
                {
                    nf.Segments.Add(new LineSegment(WarpPoint(lseg.Point.X, lseg.Point.Y, w, h, o), lseg.IsStroked));
                }
            }
            result.Figures.Add(nf);
        }
        return result;
    }

    private static Geometry CreateTailGeometry(BubbleTail tail)
    {
        var start = new Point(tail.StartX, tail.StartY);
        var mid = new Point(tail.MidX, tail.MidY);
        var end = new Point(tail.X, tail.Y);

        // 생각 말풍선 꼬리: 곡선 대신 시작→(중간 제어점)→끝 곡선을 따라 점점 작아지는 원 3개.
        if (tail.ThoughtTail)
        {
            return CreateThoughtTailGeometry(start, mid, end, tail.Width);
        }

        var direction = end - start;

        if (direction.Length < 1)
        {
            direction = new Vector(0, 1);
        }

        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var halfWidth = Math.Max(2, tail.Width / 2);
        var startA = start + normal * halfWidth;
        var startB = start - normal * halfWidth;

        // 중간 점을 곡선의 제어점으로 사용한다. 베이스의 양쪽 변은
        // 중간 점을 기준으로 ±halfWidth만큼 벌어진 제어점을 지나 끝점(꼭짓점)으로 모인다.
        var controlA = mid + normal * halfWidth;
        var controlB = mid - normal * halfWidth;

        var figure = new PathFigure { StartPoint = startA, IsClosed = true };
        figure.Segments.Add(new QuadraticBezierSegment(controlA, end, true));
        figure.Segments.Add(new QuadraticBezierSegment(controlB, startB, true));
        return new PathGeometry(new[] { figure });
    }

    // 생각 말풍선 꼬리: 시작→(중간 제어점)→끝 이차 베지어 곡선을 따라 점점 작아지는 원 3개(본체 쪽이 가장 큼).
    private static Geometry CreateThoughtTailGeometry(Point start, Point mid, Point end, double width)
    {
        var baseR = Math.Max(6.0, width * 0.9); // 가장 큰 원 반지름(굵기 기준).
        var t = new[] { 0.4, 0.7, 1.0 };        // 시작점은 본체 안일 수 있어 살짝 바깥(0.4)부터 끝(1.0)까지.
        var rf = new[] { 1.0, 0.66, 0.42 };     // 점점 작아지는 반지름 비율.

        var group = new GeometryGroup();
        for (var i = 0; i < 3; i++)
        {
            var c = QuadBezierPoint(start, mid, end, t[i]); // 중간 핸들을 제어점으로 한 곡선 위 위치.
            var r = baseR * rf[i];
            group.Children.Add(new EllipseGeometry(c, r, r));
        }
        return group;
    }

    // 이차 베지어 곡선 위 점: (1-t)²·P0 + 2(1-t)t·P1 + t²·P2.
    private static Point QuadBezierPoint(Point p0, Point p1, Point p2, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X,
            u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y);
    }

    private static Thumb CreateTailHandle(Color? color = null)
    {
        return new Thumb
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.SizeAll,
            Background = new SolidColorBrush(color ?? Color.FromRgb(43, 111, 106)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Hidden
        };
    }

    private void DragSelectedTailPoint(TailPointKind point, DragDeltaEventArgs e)
    {
        if (_selectedBubble == null || _selectedBubbleTail == null)
        {
            return;
        }

        switch (point)
        {
            case TailPointKind.Start:
                // 넓은 쪽(밑변)을 끌면 꼬리 전체를 같은 변위로 통째로 옮긴다.
                _selectedBubbleTail.StartX += e.HorizontalChange;
                _selectedBubbleTail.StartY += e.VerticalChange;
                _selectedBubbleTail.MidX += e.HorizontalChange;
                _selectedBubbleTail.MidY += e.VerticalChange;
                _selectedBubbleTail.X += e.HorizontalChange;
                _selectedBubbleTail.Y += e.VerticalChange;
                break;
            case TailPointKind.Mid:
                _selectedBubbleTail.MidX += e.HorizontalChange;
                _selectedBubbleTail.MidY += e.VerticalChange;
                break;
            default:
                _selectedBubbleTail.X += e.HorizontalChange;
                _selectedBubbleTail.Y += e.VerticalChange;
                break;
        }

        // 끝점 좌표가 바뀌는 경우(끝점 이동, 또는 시작점 이동으로 전체가 따라온 경우) 끝점 슬라이더를 동기화한다.
        if (point == TailPointKind.Start || point == TailPointKind.End)
        {
            _isLoadingInspector = true;
            BubbleTailXSlider.Value = Math.Clamp(_selectedBubbleTail.X, BubbleTailXSlider.Minimum, BubbleTailXSlider.Maximum);
            BubbleTailYSlider.Value = Math.Clamp(_selectedBubbleTail.Y, BubbleTailYSlider.Minimum, BubbleTailYSlider.Maximum);
            BubbleTailWidthSlider.Value = Math.Clamp(_selectedBubbleTail.Width, BubbleTailWidthSlider.Minimum, BubbleTailWidthSlider.Maximum);
            _isLoadingInspector = false;
        }

        UpdateBubbleGeometry(_selectedBubble);
        UpdateBubbleTailList(_selectedBubble);
        UpdateInspectorLabels();
    }

    // 호출부 호환용: 인자 말풍선과 무관하게 선택된 꼬리에 대해 싱글톤 핸들을 갱신한다.
    private void UpdateBubbleTailHandles(SpeechBubble bubble)
    {
        PositionSelectedTailHandles();
    }

    private void PositionSelectedTailHandles()
    {
        PositionBubbleSelectionBox();
        PositionImageSelectionBox();
        PositionTextRegionHandles();
        PositionPanelCornerHandles();
        PositionBubbleCornerHandles();
        EnsureTailHandles();

        var tail = _selectedBubbleTail;
        var show = _selectedBubble != null && tail != null && _selectedBubble.Tails.Contains(tail);
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        _tailStartHandle!.Visibility = visibility;
        _tailMidHandle!.Visibility = visibility;
        _tailEndHandle!.Visibility = visibility;

        if (!show || _selectedBubble == null || tail == null)
        {
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        PlaceTailHandle(_tailStartHandle, origin.X + tail.StartX, origin.Y + tail.StartY);
        PlaceTailHandle(_tailMidHandle, origin.X + tail.MidX, origin.Y + tail.MidY);
        PlaceTailHandle(_tailEndHandle, origin.X + tail.X, origin.Y + tail.Y);
    }

    private static void PlaceTailHandle(Thumb handle, double pageX, double pageY)
    {
        Canvas.SetLeft(handle, pageX - handle.Width / 2);
        Canvas.SetTop(handle, pageY - handle.Height / 2);
    }

    // 선택 박스/리사이즈 핸들은 PageOverlay(비클리핑)에 두어 칸 경계를 넘는 말풍선도 잘리지 않고 보인다.
    private void EnsureBubbleSelectionUi()
    {
        if (_bubbleSelectionBox == null)
        {
            _bubbleSelectionBox = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 111, 106)),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Hidden
            };

        }

        if (!PageOverlay.Children.Contains(_bubbleSelectionBox))
        {
            PageOverlay.Children.Add(_bubbleSelectionBox);
            Panel.SetZIndex(_bubbleSelectionBox, int.MaxValue - 2);
        }
    }

    private void PositionBubbleSelectionBox()
    {
        EnsureBubbleSelectionUi();

        var show = _selectionKind == SelectionKind.Bubble && _selectedBubble != null;
        _bubbleSelectionBox!.Visibility = show ? Visibility.Visible : Visibility.Hidden;

        if (!show || _selectedBubble == null)
        {
            // 핸들도 함께 숨긴다.
            PositionBubbleResizeHandles(false, 0, 0, 0, 0, SelectionAccentBrush);
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;

        // 잠긴 말풍선은 선택 박스/핸들을 빨강 계열로 구분한다.
        var accent = _selectedBubble.IsLocked ? SelectionLockedBrush : SelectionAccentBrush;
        _bubbleSelectionBox.BorderBrush = accent;

        Canvas.SetLeft(_bubbleSelectionBox, origin.X);
        Canvas.SetTop(_bubbleSelectionBox, origin.Y);
        _bubbleSelectionBox.Width = w;
        _bubbleSelectionBox.Height = h;

        // 8방향 리사이즈 핸들을 박스 8지점에 배치(잠긴 말풍선도 표시하되 빨강).
        PositionBubbleResizeHandles(true, origin.X, origin.Y, w, h, accent);
    }

    private void EnsureTextRegionHandles()
    {
        if (_textRegionTopLeft == null)
        {
            // 텍스트 영역 모서리 핸들(선택 박스의 틸과 구분되도록 주황색).
            var color = Color.FromRgb(214, 122, 32);
            _textRegionTopLeft = CreateCornerHandle(color, Cursors.SizeNWSE);
            _textRegionTopRight = CreateCornerHandle(color, Cursors.SizeNESW);
            _textRegionBottomLeft = CreateCornerHandle(color, Cursors.SizeNESW);
            _textRegionBottomRight = CreateCornerHandle(color, Cursors.SizeNWSE);
            _textRegionTopLeft.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.TopLeft, e);
            _textRegionTopRight!.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.TopRight, e);
            _textRegionBottomLeft!.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.BottomLeft, e);
            _textRegionBottomRight!.DragDelta += (_, e) => DragTextRegionCorner(TextRegionCorner.BottomRight, e);
        }

        foreach (var handle in new[] { _textRegionTopLeft!, _textRegionTopRight!, _textRegionBottomLeft!, _textRegionBottomRight! })
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    private static Thumb CreateCornerHandle(Color color, Cursor cursor)
    {
        return new Thumb
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = cursor,
            Background = new SolidColorBrush(color),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Visibility = Visibility.Hidden
        };
    }

    private void PositionTextRegionHandles()
    {
        EnsureTextRegionHandles();

        var show = _selectionKind == SelectionKind.Bubble && _selectedBubble != null;
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        _textRegionTopLeft!.Visibility = visibility;
        _textRegionTopRight!.Visibility = visibility;
        _textRegionBottomLeft!.Visibility = visibility;
        _textRegionBottomRight!.Visibility = visibility;

        if (!show || _selectedBubble == null)
        {
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;
        var m = _selectedBubble.TextBlock.Margin;

        // 텍스트 영역 사각형(컨테이너 - 여백)의 네 모서리.
        var leftX = origin.X + m.Left;
        var rightX = origin.X + w - m.Right;
        var topY = origin.Y + m.Top;
        var bottomY = origin.Y + h - m.Bottom;

        PlaceTailHandle(_textRegionTopLeft, leftX, topY);
        PlaceTailHandle(_textRegionTopRight, rightX, topY);
        PlaceTailHandle(_textRegionBottomLeft, leftX, bottomY);
        PlaceTailHandle(_textRegionBottomRight, rightX, bottomY);
    }

    private void DragTextRegionCorner(TextRegionCorner corner, DragDeltaEventArgs e)
    {
        if (_selectedBubble == null)
        {
            return;
        }

        var tb = _selectedBubble.TextBlock;
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;
        double left = tb.Margin.Left, top = tb.Margin.Top, right = tb.Margin.Right, bottom = tb.Margin.Bottom;

        switch (corner)
        {
            case TextRegionCorner.TopLeft:
                left += e.HorizontalChange;
                top += e.VerticalChange;
                break;
            case TextRegionCorner.TopRight:
                right -= e.HorizontalChange;
                top += e.VerticalChange;
                break;
            case TextRegionCorner.BottomLeft:
                left += e.HorizontalChange;
                bottom -= e.VerticalChange;
                break;
            default:
                right -= e.HorizontalChange;
                bottom -= e.VerticalChange;
                break;
        }

        // 여백은 0 이상, 텍스트 영역이 최소 10px는 남도록 제한.
        const double minRegion = 10;
        left = Math.Clamp(left, 0, Math.Max(0, w - right - minRegion));
        right = Math.Clamp(right, 0, Math.Max(0, w - left - minRegion));
        top = Math.Clamp(top, 0, Math.Max(0, h - bottom - minRegion));
        bottom = Math.Clamp(bottom, 0, Math.Max(0, h - top - minRegion));

        tb.Margin = new Thickness(left, top, right, bottom);
        ApplyBubbleAutoFit(_selectedBubble);   // 줄어든 텍스트 영역에 맞춰 폰트 재축소(넘침/잘림 방지).
        ApplyBubbleTextWarp(_selectedBubble);  // 여백이 바뀌었으니 워프 매핑도 갱신.
        PositionTextRegionHandles();
    }

    private void EnsurePanelCornerHandles()
    {
        if (_panelCornerHandles == null)
        {
            var color = Color.FromRgb(43, 111, 106);
            _panelCornerHandles = new[]
            {
                CreateCornerHandle(color, Cursors.SizeNWSE), // TL
                CreateCornerHandle(color, Cursors.SizeNESW), // TR
                CreateCornerHandle(color, Cursors.SizeNWSE), // BR
                CreateCornerHandle(color, Cursors.SizeNESW)  // BL
            };
            for (var i = 0; i < 4; i++)
            {
                var index = i;
                _panelCornerHandles[i].DragDelta += (_, e) => DragPanelCorner(index, e);
            }
        }

        foreach (var handle in _panelCornerHandles)
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    private void PositionPanelCornerHandles()
    {
        EnsurePanelCornerHandles();

        // 8방향 리사이즈 핸들도 함께 갱신한다(사변형 모드가 아닐 때만 표시).
        PositionPanelResizeHandles();

        var show = _selectionKind == SelectionKind.Panel && _selectedPanel != null && _selectedPanel.CornerMode;
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        foreach (var handle in _panelCornerHandles!)
        {
            handle.Visibility = visibility;
        }

        if (!show || _selectedPanel == null)
        {
            return;
        }

        var w = _selectedPanel.Frame.Width;
        var h = _selectedPanel.Frame.Height;
        var ox = GetCanvasLeft(_selectedPanel.Frame);
        var oy = GetCanvasTop(_selectedPanel.Frame);
        var o = _selectedPanel.CornerOffsets;

        // TL,TR,BR,BL (CornerOffsets 순서와 동일)
        PlaceTailHandle(_panelCornerHandles[0], ox + 0 + o[0].X, oy + 0 + o[0].Y);
        PlaceTailHandle(_panelCornerHandles[1], ox + w + o[1].X, oy + 0 + o[1].Y);
        PlaceTailHandle(_panelCornerHandles[2], ox + w + o[2].X, oy + h + o[2].Y);
        PlaceTailHandle(_panelCornerHandles[3], ox + 0 + o[3].X, oy + h + o[3].Y);
    }

    private void DragPanelCorner(int index, DragDeltaEventArgs e)
    {
        if (_selectedPanel == null || index < 0 || index > 3)
        {
            return;
        }

        var o = _selectedPanel.CornerOffsets;
        o[index] = new Point(o[index].X + e.HorizontalChange, o[index].Y + e.VerticalChange);

        UpdatePanelShape(_selectedPanel);
        PositionPanelCornerHandles();
    }

    // --- 말풍선 모서리 조절 핸들(보라색, 칸 핸들과 구분) ---

    private Thumb[]? _bubbleCornerHandles;

    private void EnsureBubbleCornerHandles()
    {
        if (_bubbleCornerHandles == null)
        {
            var color = Color.FromRgb(150, 70, 180); // 보라색
            _bubbleCornerHandles = new[]
            {
                CreateCornerHandle(color, Cursors.SizeNWSE), // TL
                CreateCornerHandle(color, Cursors.SizeNESW), // TR
                CreateCornerHandle(color, Cursors.SizeNWSE), // BR
                CreateCornerHandle(color, Cursors.SizeNESW)  // BL
            };
            for (var i = 0; i < 4; i++)
            {
                var index = i;
                _bubbleCornerHandles[i].DragDelta += (_, e) => DragBubbleCorner(index, e);
            }
        }

        foreach (var handle in _bubbleCornerHandles)
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue - 1);
            }
        }
    }

    private void PositionBubbleCornerHandles()
    {
        EnsureBubbleCornerHandles();

        // 모서리 조절(도형/글자 중 하나라도 ON)일 때만 핸들을 보인다.
        var show = _selectionKind == SelectionKind.Bubble && _selectedBubble != null
                   && (_selectedBubble.WarpShape || _selectedBubble.WarpText);
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        foreach (var handle in _bubbleCornerHandles!)
        {
            handle.Visibility = visibility;
        }

        if (!show || _selectedBubble == null)
        {
            return;
        }

        var origin = GetBubblePageOrigin(_selectedBubble);
        var w = _selectedBubble.Container.Width;
        var h = _selectedBubble.Container.Height;
        var o = _selectedBubble.CornerOffsets;

        // TL,TR,BR,BL (CornerOffsets 순서와 동일)
        PlaceTailHandle(_bubbleCornerHandles[0], origin.X + 0 + o[0].X, origin.Y + 0 + o[0].Y);
        PlaceTailHandle(_bubbleCornerHandles[1], origin.X + w + o[1].X, origin.Y + 0 + o[1].Y);
        PlaceTailHandle(_bubbleCornerHandles[2], origin.X + w + o[2].X, origin.Y + h + o[2].Y);
        PlaceTailHandle(_bubbleCornerHandles[3], origin.X + 0 + o[3].X, origin.Y + h + o[3].Y);
    }

    private void DragBubbleCorner(int index, DragDeltaEventArgs e)
    {
        if (_selectedBubble == null || index < 0 || index > 3)
        {
            return;
        }

        var o = _selectedBubble.CornerOffsets;
        o[index] = new Point(o[index].X + e.HorizontalChange, o[index].Y + e.VerticalChange);
        _historyDirty = true;

        UpdateBubbleGeometry(_selectedBubble); // 도형(+글자 워프 파라미터) 갱신.
        PositionBubbleCornerHandles();
    }

    // 말풍선 컨테이너의 (0,0)을 페이지(PageOverlay) 좌표로 변환한다.
    // 크롭된 말풍선은 칸 오버레이에 들어 있으므로 칸 오버레이 원점을 더해 준다.
    private Point GetBubblePageOrigin(SpeechBubble bubble)
    {
        // 말풍선은 (크롭 여부와 무관하게) 칸 안의 오버레이에 있으므로, 그 오버레이 원점을 페이지 좌표로 변환한다.
        var overlay = bubble.IsCropped ? bubble.OwnerPanel.Overlay : bubble.OwnerPanel.FreeOverlay;
        var panelOrigin = overlay.TransformToVisual(PageOverlay).Transform(new Point(0, 0));
        return new Point(panelOrigin.X + GetCanvasLeft(bubble.Container), panelOrigin.Y + GetCanvasTop(bubble.Container));
    }

    private static System.Windows.Shapes.Path CreateBubbleOutlinePath()
    {
        return new System.Windows.Shapes.Path
        {
            Fill = Brushes.Transparent,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
    }

    private static System.Windows.Shapes.Path CreateBubbleFillPath()
    {
        return new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Stroke = Brushes.Transparent,
            StrokeThickness = 0,
            IsHitTestVisible = false
        };
    }

    private void EnsureTailHandles()
    {
        if (_tailStartHandle == null)
        {
            _tailStartHandle = CreateTailHandle(Color.FromRgb(43, 111, 106));  // 시작: 청록
            _tailMidHandle = CreateTailHandle(Color.FromRgb(214, 122, 32));    // 중간: 주황
            _tailEndHandle = CreateTailHandle(Color.FromRgb(46, 110, 200));    // 끝: 파랑
            _tailStartHandle.DragDelta += (_, e) => DragSelectedTailPoint(TailPointKind.Start, e);
            _tailMidHandle!.DragDelta += (_, e) => DragSelectedTailPoint(TailPointKind.Mid, e);
            _tailEndHandle!.DragDelta += (_, e) => DragSelectedTailPoint(TailPointKind.End, e);
        }

        // 핸들은 페이지 좌표로 배치하므로 페이지 레이어에 올려, 칸 경계 클리핑을 받지 않게 한다.
        foreach (var handle in new[] { _tailStartHandle!, _tailMidHandle!, _tailEndHandle! })
        {
            if (!PageOverlay.Children.Contains(handle))
            {
                handle.HorizontalAlignment = HorizontalAlignment.Left;
                handle.VerticalAlignment = VerticalAlignment.Top;
                PageOverlay.Children.Add(handle);
                Panel.SetZIndex(handle, int.MaxValue);
            }
        }
    }

    private BubbleShape GetSelectedBubbleShape()
    {
        if (BubbleShapeComboBox?.SelectedItem is not ComboBoxItem item)
        {
            return BubbleShape.RoundRect;
        }

        return item.Tag?.ToString() switch
        {
            "CloudExplosion" => BubbleShape.CloudExplosion,
            "Flash" => BubbleShape.Flash,
            "ConcentrationLines" => BubbleShape.ConcentrationLines,
            "EffectLines" => BubbleShape.EffectLines,
            "None" => BubbleShape.None,
            _ => BubbleShape.RoundRect
        };
    }

    // 저장된 모양 문자열을 현재 모양으로 변환한다. 구버전 값은 적절한 강도로 매핑한다.
    private static (BubbleShape Shape, double? LegacyStrength) MapShape(string? raw)
    {
        return raw switch
        {
            "CloudExplosion" => (BubbleShape.CloudExplosion, null),
            "Shout" => (BubbleShape.CloudExplosion, null), // 삭제된 파도/외침 → 구름/폭발로 대체(구버전 호환).
            "Flash" => (BubbleShape.Flash, null),
            "ConcentrationLines" => (BubbleShape.ConcentrationLines, null),
            "EffectLines" => (BubbleShape.EffectLines, null),
            "None" => (BubbleShape.None, null),
            "Oval" => (BubbleShape.RoundRect, 0.0),
            "Rectangle" => (BubbleShape.RoundRect, 100.0),
            "Cloud" => (BubbleShape.CloudExplosion, 0.0),
            "Explosion" => (BubbleShape.CloudExplosion, 100.0),
            _ => (BubbleShape.RoundRect, null)
        };
    }

    // 원형/사각: 강도 0이면 모서리 반지름이 절반(=타원), 100이면 0(=사각), 중간은 둥근 사각형.
}
