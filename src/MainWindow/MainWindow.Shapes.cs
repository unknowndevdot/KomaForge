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
    private static Geometry CreateRoundRectGeometry(double width, double height, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        // 강도에 따라 타원(0) → 캡슐(50) → 사각형(100)으로 블렌딩한다.
        //  - 0~50: 타원 반경(너비/높이 각각)에서 '짧은 변 기준 균일 반경(캡슐)'으로 보간 → 점점 모서리가 일정해진다.
        //  - 50~100: 캡슐(균일 반경)을 0으로 균일하게 줄여 사각형이 된다 → 모서리 곡선이 비율과 무관하게 일정.
        var rCapsule = Math.Min(width, height) / 2.0;
        double rx, ry;
        if (t <= 0.5)
        {
            var b = t / 0.5;
            rx = width / 2.0 + (rCapsule - width / 2.0) * b;
            ry = height / 2.0 + (rCapsule - height / 2.0) * b;
        }
        else
        {
            var c = (t - 0.5) / 0.5;
            rx = ry = rCapsule * (1.0 - c);
        }
        var geometry = new RectangleGeometry(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)), rx, ry);
        geometry.Freeze();
        return geometry;
    }

    // 불규칙도(0~100)를 흔들림/지터 배율로. 50이면 1.0(기존 기본 흔들림), 0이면 균일, 100이면 2배.
    private static double IrregularityMul(double irregularity) => Math.Clamp(irregularity, 0, 100) / 50.0;

    // 구름/폭발: 강도 0이면 완전 볼록(구름), 100이면 완전 오목(폭발). 부드러운 곡선.
    private static Geometry CreateCloudExplosionGeometry(double width, double height, int count, double strength, double irregularity, double widthVariation)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var baseRadiusFactor = 0.55 + 0.40 * t;   // 볼록(작은 베이스) → 오목(큰 베이스)
        var pushFactor = 1.7 - 3.4 * t;           // 바깥(+1.7, 더 볼록) → 안쪽 깊게(-1.7, 더 오목)
        return CreateLobedGeometry(width, height, count, baseRadiusFactor, pushFactor, false, irregularity, widthVariation);
    }

    // 플래시(충격) 말풍선: 타원 코어 둘레에 가는 방사형 가시가 촘촘히 뻗어 나온 모양.
    // 돌기 수 = 가시 수, 강도 = 가시 길이(0이면 원형), 불규칙도 = 가시 길이 편차(클수록 들쭉날쭉한 털 느낌).
    private static Geometry CreateFlashGeometry(double width, double height, int count, double strength, double irregularity)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var m = IrregularityMul(irregularity);   // 0~2 (50이면 1)
        var spikes = Math.Max(8, count);  // 가시 수 = 돌기 수.
        var cx = width / 2.0;
        var cy = height / 2.0;
        var hx = width / 2.0;
        var hy = height / 2.0;
        const double coreFactor = 0.60;        // 안쪽 타원(가시 골) 반지름 비율
        // 가시 최대 길이(절대, 짧은 반지름 기준): 강도 0이면 0 → 코어 타원만 남아 원형이 된다.
        var maxSpike = t * 0.95 * Math.Min(hx, hy);
        var start = -Math.PI / 2.0;

        Point Core(double angle) => new Point(
            cx + hx * coreFactor * Math.Cos(angle),
            cy + hy * coreFactor * Math.Sin(angle));

        // 코어 타원 위 점에서 중심 반대 방향(방사형)으로 length만큼 뻗은 가시 끝점.
        Point Tip(double angle, double length)
        {
            var dx = hx * Math.Cos(angle);
            var dy = hy * Math.Sin(angle);
            var len = Math.Sqrt(dx * dx + dy * dy);
            var core = Core(angle);
            return new Point(core.X + dx / len * length, core.Y + dy / len * length);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Core(start), true, true);
            for (var i = 0; i < spikes; i++)
            {
                var peakAngle = start + (i + 0.5) * 2.0 * Math.PI / spikes;
                var nextValley = start + (i + 1) * 2.0 * Math.PI / spikes;
                // 불규칙도: 가시마다 길이를 0~최대 사이에서 제각각으로(0이면 모두 최대=균일, 클수록 짧은 가시가 섞여 털처럼 들쭉날쭉).
                var rnd = Pseudo(i * 12.9898 + 7.13);
                var lenFactor = Math.Clamp(1.0 - 0.5 * m * rnd, 0.0, 1.0);
                context.LineTo(Tip(peakAngle, maxSpike * lenFactor), true, false);
                context.LineTo(Core(nextValley), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 선 효과(집중선/효과선)인지. 이런 모양은 채움이 아니라 열린 선(스트로크)으로 그리며 꼬리를 합치지 않는다.
    private static bool IsLineEffectShape(BubbleShape shape) =>
        shape == BubbleShape.ConcentrationLines || shape == BubbleShape.EffectLines;

    // 결정적 의사난수(0~1). 다시 그려도 같은 모양이 나오도록 index 기반으로 만든다.
    private static double Pseudo(double seed)
    {
        var v = Math.Sin(seed * 12.9898) * 43758.5453;
        return v - Math.Floor(v);
    }

    // 시작점(start)은 투명, 진행 방향(end)으로 갈수록 불투명한 선형 그라데이션. 처음 1/6 구간은 투명을 유지.
    private static Brush CreateDirectionFadeBrush(Brush lineBrush, Point start, Point end)
    {
        var color = (lineBrush as SolidColorBrush)?.Color ?? Colors.Black;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = start,
            EndPoint = end
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0.0));      // 시작: 투명
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0 / 6));  // 1/6까지 투명 유지
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 1.0));    // 끝: 불투명
        brush.Freeze();
        return brush;
    }

    // 양쪽 끝을 모두 투명하게(가운데 불투명) 페이드하는 브러시 — 속도선 '양쪽 페이드' 옵션용.
    private static Brush CreateBothSidesFadeBrush(Brush lineBrush, Point start, Point end)
    {
        var color = (lineBrush as SolidColorBrush)?.Color ?? Colors.Black;
        var clear = Color.FromArgb(0, color.R, color.G, color.B);
        var solid = Color.FromArgb(255, color.R, color.G, color.B);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = start,
            EndPoint = end
        };
        brush.GradientStops.Add(new GradientStop(clear, 0.0));   // 한쪽 끝: 투명
        brush.GradientStops.Add(new GradientStop(solid, 0.3));   // 안쪽: 불투명
        brush.GradientStops.Add(new GradientStop(solid, 0.7));   // 안쪽: 불투명
        brush.GradientStops.Add(new GradientStop(clear, 1.0));   // 반대쪽 끝: 투명
        brush.Freeze();
        return brush;
    }

    // 집중선: 중앙을 향하는 방사형 직선들. 돌기 ×10.
    // 선의 기하(시작/끝점)는 강도와 무관하게 고정이고, 강도는 페이드(중앙의 투명 반지름)만 키운다.
    // 투명 경계는 박스 비율과 무관한 정원, 선 끝은 사각형 가장자리.
    // (Inner = 중앙 쪽 시작점, Edge = 바깥 끝점, FadeStart/FadeEnd = 투명→불투명 그라데이션 절대 위치)
    private static List<(Point Inner, Point Edge, Point FadeStart, Point FadeEnd)> ConcentrationLineEndpoints(double width, double height, int count, double strength, double irregularity)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var m = IrregularityMul(irregularity);
        var lines = Math.Max(8, count * 10);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var hx = width / 2.0;
        var hy = height / 2.0;
        var minR = Math.Min(hx, hy);
        // 불규칙도: 시작 반지름을 들쭉날쭉하게(0이면 모두 중앙에서 시작). 강도와 독립.
        var variationAmp = 0.35 * m;
        // 강도: 중앙에서부터 완전 투명한 반지름(정원). 선 기하는 건드리지 않으므로 강도 조절로 선이 움직이지 않는다.
        var fadeStartR = 0.62 * t * minR;
        var fadeEndR = fadeStartR + 0.45 * minR;
        var start = -Math.PI / 2.0;
        var step = 2.0 * Math.PI / lines;

        var result = new List<(Point, Point, Point, Point)>(lines);
        for (var i = 0; i < lines; i++)
        {
            // 각도 간격을 불규칙도에 비례해 흔들어 선 사이 간격이 일정하지 않게 한다(0이면 균일).
            var angle = start + i * step + (Pseudo(i * 1.7 + 0.2) - 0.5) * step * 1.4 * m;
            var dx = Math.Cos(angle);
            var dy = Math.Sin(angle);
            // 시작 반지름(정원 기준): 불규칙도만 반영해 길고 짧은 선이 섞인다.
            var innerR = minR * Math.Clamp(Pseudo(i + 0.3) * variationAmp, 0.0, 0.95);
            var inner = new Point(cx + innerR * dx, cy + innerR * dy);
            // 같은 방사 방향으로 사각형 가장자리에 닿는 거리(가로/세로 변 중 먼저 닿는 쪽).
            var edgeR = Math.Min(hx / Math.Abs(dx), hy / Math.Abs(dy));
            var edge = new Point(cx + edgeR * dx, cy + edgeR * dy);
            // 시작점이 공통 페이드 반지름보다 바깥이면(불규칙도로 짧아진 선) 자기 시작점부터 페이드해
            // 선이 딱 끊기지 않고 항상 서서히 나타난다.
            var lineFadeStartR = Math.Max(fadeStartR, innerR);
            var fadeStart = new Point(cx + lineFadeStartR * dx, cy + lineFadeStartR * dy);
            var clampedEndR = Math.Min(lineFadeStartR + (fadeEndR - fadeStartR), edgeR);
            var fadeEnd = new Point(cx + clampedEndR * dx, cy + clampedEndR * dy);
            result.Add((inner, edge, fadeStart, fadeEnd));
        }

        return result;
    }

    // BodyPath.Data용(테두리 없음 판정 등). 실제 그리기는 선마다 BuildConcentrationLineHost에서 한다.
    private static Geometry CreateConcentrationLinesGeometry(double width, double height, int count, double strength, double irregularity)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (inner, edge, _, _) in ConcentrationLineEndpoints(width, height, count, strength, irregularity))
            {
                context.BeginFigure(inner, false, false);
                context.LineTo(edge, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 집중선을 선마다 개별 Path로 만들어 선 호스트에 채운다.
    // 페이드는 선 길이 비율이 아니라 중심 기준 절대 반지름(FadeStart→FadeEnd)으로 칠해
    // 투명 경계가 박스 비율과 무관한 정원이 된다.
    private static void BuildConcentrationLineHost(SpeechBubble bubble)
    {
        var host = bubble.LineHost;
        host.Children.Clear();

        var w = bubble.Container.Width;
        var h = bubble.Container.Height;
        var lineColor = bubble.TextBlock.Fill;
        var warp = bubble.WarpShape && HasCornerWarp(bubble.CornerOffsets);
        var o = bubble.CornerOffsets;

        foreach (var (inner, edge, fadeStart, fadeEnd) in ConcentrationLineEndpoints(w, h, bubble.ShapeCount, bubble.ShapeStrength, bubble.ShapeIrregularity))
        {
            // 모서리 조절(도형): 선 양 끝점을 사변형으로 워프한다(직선이라 끝점만 옮겨도 충분).
            var a = warp ? WarpPoint(inner.X, inner.Y, w, h, o) : inner;
            var b = warp ? WarpPoint(edge.X, edge.Y, w, h, o) : edge;
            var fs = warp ? WarpPoint(fadeStart.X, fadeStart.Y, w, h, o) : fadeStart;
            var fe = warp ? WarpPoint(fadeEnd.X, fadeEnd.Y, w, h, o) : fadeEnd;
            var path = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(a, b),
                // 중심 쪽(fadeStart 이전) 투명 → 바깥(fadeEnd 이후) 불투명.
                Stroke = CreateDirectionFadeBrush(lineColor, fs, fe),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            host.Children.Add(path);
        }
    }

    // 속도선(효과선): 한 방향으로 직진하는 일직선 평행선들. 강도 0~100 → 방향 0~360도.
    // 각 선의 길이가 제각각이고 선 사이 간격도 일정하지 않다. 돌기 ×10.
    // (베이스 = -d쪽 시작점, 팁 = 진행 방향 끝점) 목록을 만든다.
    private static List<(Point Base, Point Tip)> EffectLineEndpoints(double width, double height, int count, double strength, double irregularity, bool centered = false)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var m = IrregularityMul(irregularity);
        var lines = Math.Max(8, count * 10);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var angle = t * 2.0 * Math.PI;                 // 0~360도
        var dx = Math.Cos(angle);
        var dy = Math.Sin(angle);
        var px = -dy;                                  // 진행 방향의 수직(선 간격 축)
        var py = dx;

        var span = Math.Sqrt(width * width + height * height); // 박스를 항상 가로지르도록 대각선 길이
        var half = span / 2.0;
        var spacing = span / lines;

        var result = new List<(Point, Point)>(lines);
        for (var i = 0; i < lines; i++)
        {
            // 수직 위치(선 간격)를 불규칙도에 비례해 흔든다(0이면 균일 간격).
            var perp = -half + (i + 0.5) * spacing + (Pseudo(i * 3.1 + 0.7) - 0.5) * spacing * 1.4 * m;
            // 길이를 들쭉날쭉하게(0이면 모두 가득 차게, 50이면 기존 0.30~1.00 범위). 1.0(가득)에서 불규칙도만큼 짧아진다.
            var raw = 0.30 + 0.70 * Pseudo(i * 5.3 + 0.9);
            var lenFrac = Math.Clamp(1.0 + (raw - 1.0) * m, 0.1, 1.5);
            var length = span * lenFrac;
            // 양쪽 페이드(centered)면 각 선을 중앙 기준 ±length/2로 배치(중앙 정렬·좌우 대칭). 아니면 한쪽 끝(-half)에서 시작.
            var a0 = centered ? -length / 2.0 : -half;
            var a1 = centered ? length / 2.0 : -half + length;
            var baseP = new Point(cx + dx * a0 + px * perp, cy + dy * a0 + py * perp);
            var tip = new Point(cx + dx * a1 + px * perp, cy + dy * a1 + py * perp);
            result.Add((baseP, tip));
        }

        return result;
    }

    // BodyPath.Data용(테두리 없음 판정 등). 실제 그리기는 선마다 BuildEffectLineHost에서 한다.
    private static Geometry CreateEffectLinesGeometry(double width, double height, int count, double strength, double irregularity)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (baseP, tip) in EffectLineEndpoints(width, height, count, strength, irregularity))
            {
                context.BeginFigure(baseP, false, false);
                context.LineTo(tip, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 속도선을 선마다 개별 Path로 만들어 선 호스트에 채운다. 각 선은 박스로 클립한 '보이는 구간' 기준으로
    // 팁(앞쪽)은 투명, 베이스(뒤쪽)는 불투명하게 자기만의 그라데이션을 갖는다(선마다 시작점 기준 페이드).
    private static void BuildEffectLineHost(SpeechBubble bubble)
    {
        var host = bubble.LineHost;
        host.Children.Clear();

        var w = bubble.Container.Width;
        var h = bubble.Container.Height;
        var lineColor = bubble.TextBlock.Fill;
        var warp = bubble.WarpShape && HasCornerWarp(bubble.CornerOffsets);
        var o = bubble.CornerOffsets;

        foreach (var (baseP, tip) in EffectLineEndpoints(w, h, bubble.ShapeCount, bubble.ShapeStrength, bubble.ShapeIrregularity, bubble.LineFadeBothSides))
        {
            // 베이스→팁 선분을 박스로 클립해 보이는 구간만 남긴다(c0=베이스쪽, c1=팁쪽).
            if (!ClipSegmentToBox(baseP, tip, w, h, out var c0, out var c1))
            {
                continue;
            }

            // 모서리 조절(도형): 클립된 양 끝점을 사변형으로 워프한다.
            var a = warp ? WarpPoint(c0.X, c0.Y, w, h, o) : c0;
            var b = warp ? WarpPoint(c1.X, c1.Y, w, h, o) : c1;
            var path = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(a, b),
                // 양쪽 페이드 ON이면 양 끝 모두 투명(가운데 불투명), OFF면 팁(b) 투명 → 베이스(a) 불투명.
                Stroke = bubble.LineFadeBothSides
                    ? CreateBothSidesFadeBrush(lineColor, a, b)
                    : CreateDirectionFadeBrush(lineColor, b, a),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            host.Children.Add(path);
        }
    }

    // 선분 p0→p1을 [0,w]×[0,h] 박스로 클립(Liang–Barsky). 보이면 true와 클립된 양 끝점을 돌려준다.
    private static bool ClipSegmentToBox(Point p0, Point p1, double w, double h, out Point c0, out Point c1)
    {
        double t0 = 0, t1 = 1;
        var dx = p1.X - p0.X;
        var dy = p1.Y - p0.Y;
        c0 = p0;
        c1 = p1;

        double[] p = { -dx, dx, -dy, dy };
        double[] q = { p0.X, w - p0.X, p0.Y, h - p0.Y };

        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-9)
            {
                if (q[i] < 0)
                {
                    return false; // 박스 밖에서 평행
                }

                continue;
            }

            var r = q[i] / p[i];
            if (p[i] < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
        }

        c0 = new Point(p0.X + t0 * dx, p0.Y + t0 * dy);
        c1 = new Point(p0.X + t1 * dx, p0.Y + t1 * dy);
        return true;
    }

    // 기준 타원 둘레의 점들 사이를 바깥(볼록)/안쪽(오목)으로 휜다. angular=true면 직선(각진), false면 곡선.
    private static Geometry CreateLobedGeometry(double width, double height, int count, double baseRadiusFactor, double pushFactor, bool angular, double irregularity, double widthVariation)
    {
        var n = Math.Max(3, count);
        var m = IrregularityMul(irregularity);
        // 폭 불규칙도(0~100): 돌기 위치(각도)를 모서리(대각) 쪽으로 몰아준다.
        // 변 중앙(상하좌우)은 점 간격이 벌어져 매끈해지고, 모서리에는 돌기가 촘촘히 모인다.
        var cornerBias = Math.Clamp(widthVariation, 0, 100) / 100.0;
        var cx = width / 2.0;
        var cy = height / 2.0;
        // 시작점을 최상단(변 중앙=사분면 경계)이 아니라 대각(모서리) 방향에서 시작한다.
        // 변 중앙에 점이 고정되지 않아 폭 불규칙도가 상단까지 고르게 모서리로 끌어당긴다.
        var start = -Math.PI / 2.0 + Math.PI / 4.0;
        var minDist = Math.Min(width, height) * 0.05;
        // 돌출/패임 깊이는 타원이어도 둘레 어디서나 같도록 절대 길이로 통일한다(플래시 가시와 같은 방식).
        // 짧은 반지름 기준 원에서의 인접 점 간 거리(코드 길이)를 공통 기준으로 쓴다.
        var uniformChord = 2.0 * baseRadiusFactor * (Math.Min(width, height) / 2.0) * Math.Sin(Math.PI / n);

        // 각도를 사분면(변 중앙~변 중앙) 안에서 대각(모서리) 쪽으로 끌어당긴다.
        // x^3 보간이라 cornerBias가 클수록 모서리(중앙)에 밀집하고 변 중앙(끝)은 비워진다.
        double WarpTowardCorner(double a)
        {
            if (cornerBias <= 0) return a;
            var quad = Math.Floor(a / (Math.PI / 2.0));
            var baseA = quad * (Math.PI / 2.0);
            var x = (a - baseA) / (Math.PI / 4.0) - 1.0;            // -1(변 중앙)~0(모서리)~+1(변 중앙)
            var warped = (1.0 - cornerBias) * x + cornerBias * x * x * x;
            return baseA + (warped + 1.0) * (Math.PI / 4.0);
        }

        var step = 2.0 * Math.PI / n;
        var points = new Point[n];
        for (var i = 0; i < n; i++)
        {
            // 각도와 반지름을 둘 다 불규칙도에 비례해 흔들어, 높일수록 크게 일그러진다(0이면 균일).
            // Pseudo로 점마다 제각각이라 균일한 물결이 아니라 들쭉날쭉한 구름/파도가 된다.
            var angle = WarpTowardCorner(start + i * step) + (Pseudo(i * 1.7 + 0.3) - 0.5) * step * 0.4 * m;
            var wobble = Math.Max(0.2, 1.0 - 0.40 * m * Pseudo(i * 2.9 + 0.6));
            var rx = width / 2.0 * baseRadiusFactor * wobble;
            var ry = height / 2.0 * baseRadiusFactor * wobble;
            points[i] = new Point(cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle));
        }

        // 두 점 사이 중간을 바깥/안쪽으로 민 제어점(중심을 지나치지 않게 최소 거리로 클램프).
        Point ControlBetween(Point a, Point b)
        {
            var mid = new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            var dx = mid.X - cx;
            var dy = mid.Y - cy;
            var len = Math.Max(0.0001, Math.Sqrt(dx * dx + dy * dy));
            var dist = Math.Max(minDist, len + uniformChord * pushFactor);
            return new Point(cx + dx / len * dist, cy + dy / len * dist);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (var i = 0; i < n; i++)
            {
                var next = points[(i + 1) % n];
                var control = ControlBetween(points[i], next);
                if (angular)
                {
                    context.LineTo(control, true, false);
                    context.LineTo(next, true, false);
                }
                else
                {
                    context.QuadraticBezierTo(control, next, true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

}
