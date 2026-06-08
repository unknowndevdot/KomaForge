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
        var rx = width / 2.0 * (1.0 - t);
        var ry = height / 2.0 * (1.0 - t);
        var geometry = new RectangleGeometry(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)), rx, ry);
        geometry.Freeze();
        return geometry;
    }

    // 구름/폭발: 강도 0이면 완전 볼록(구름), 100이면 완전 오목(폭발). 부드러운 곡선.
    private static Geometry CreateCloudExplosionGeometry(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var baseRadiusFactor = 0.6 + 0.35 * t;   // 볼록(작은 베이스) → 오목(큰 베이스)
        var pushFactor = 1.1 - 2.2 * t;           // 바깥(+1.1) → 안쪽 깊게(-1.1)
        return CreateLobedGeometry(width, height, count, baseRadiusFactor, pushFactor, false);
    }

    // 외침: 구름/폭발과 같은 방식이되 각진(직선) 모양. 강도가 클수록 변이 안쪽으로 더 깊이 패여(오목) 폭발하듯 보인다.
    private static Geometry CreateShoutGeometry(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        // 0이면 강하게 볼록(바깥으로 뾰족), 강도가 커질수록 줄어들어 오목으로 넘어간다.
        var baseRadiusFactor = 0.6 + 0.35 * t;
        var pushFactor = 1.8 - 3.4 * t;   // +1.8(강한 볼록) → -1.6(오목)
        return CreateLobedGeometry(width, height, count, baseRadiusFactor, pushFactor, true);
    }

    // 플래시(충격) 말풍선: 타원 코어 둘레에 가는 방사형 가시가 촘촘히 뻗어 나온 모양.
    // 돌기 수가 가시 밀도(내부적으로 ×5), 강도가 가시 길이를 정한다.
    private static Geometry CreateFlashGeometry(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var spikes = Math.Max(8, count * 10);  // 촘촘한 가시를 위해 돌기 수를 곱한다.
        var cx = width / 2.0;
        var cy = height / 2.0;
        var hx = width / 2.0;
        var hy = height / 2.0;
        const double coreFactor = 0.60;        // 안쪽 타원(가시 골) 반지름 비율
        var reach = 0.74 + 0.26 * t;           // 가시 끝(피크) 반지름 비율: 강도↑ = 더 길게
        var start = -Math.PI / 2.0;

        Point P(double angle, double factor) => new Point(
            cx + hx * factor * Math.Cos(angle),
            cy + hy * factor * Math.Sin(angle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(start, coreFactor), true, true);
            for (var i = 0; i < spikes; i++)
            {
                var peakAngle = start + (i + 0.5) * 2.0 * Math.PI / spikes;
                var nextValley = start + (i + 1) * 2.0 * Math.PI / spikes;
                // 가시 길이를 아주 살짝만 흔들어 거의 균일하게 한다.
                var wobble = 0.96 + 0.04 * Math.Abs(Math.Sin(i * 2.7 + 0.5));
                context.LineTo(P(peakAngle, reach * wobble), true, false);
                context.LineTo(P(nextValley, coreFactor), true, false);
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

    // 집중선: 중앙을 향하는 방사형 직선들. 강도가 클수록 선이 중앙에서 멀어진다. 돌기 ×10.
    // (Inner = 중앙 쪽 시작점, Edge = 바깥 끝점) 목록을 만든다.
    private static List<(Point Inner, Point Edge)> ConcentrationLineEndpoints(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
        var lines = Math.Max(8, count * 10);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var hx = width / 2.0;
        var hy = height / 2.0;
        const double outer = 1.0;          // 선 끝은 박스(타원) 가장자리까지.
        var innerBase = 0.62 * t;          // 강도↑ = 시작점이 중앙에서 멀어진다.
        var variationAmp = innerBase * 0.6; // 멀어질수록(거리=강도에 비례) 시작 반지름이 들쭉날쭉.
        var start = -Math.PI / 2.0;
        var step = 2.0 * Math.PI / lines;

        var result = new List<(Point, Point)>(lines);
        for (var i = 0; i < lines; i++)
        {
            // 각도 간격을 불규칙하게 흔들어 선 사이 간격이 일정하지 않게 한다.
            var angle = start + i * step + (Pseudo(i * 1.7 + 0.2) - 0.5) * step * 1.4;
            // 강도 0이면 모두 중앙에서 시작(균일), 강도↑이면 멀어지며 시작 반지름이 들쭉날쭉.
            var innerF = Math.Clamp(innerBase + (Pseudo(i + 0.3) - 0.5) * 2.0 * variationAmp, 0.0, 0.95);
            var inner = new Point(cx + hx * innerF * Math.Cos(angle), cy + hy * innerF * Math.Sin(angle));
            var edge = new Point(cx + hx * outer * Math.Cos(angle), cy + hy * outer * Math.Sin(angle));
            result.Add((inner, edge));
        }

        return result;
    }

    // BodyPath.Data용(테두리 없음 판정 등). 실제 그리기는 선마다 BuildConcentrationLineHost에서 한다.
    private static Geometry CreateConcentrationLinesGeometry(double width, double height, int count, double strength)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (inner, edge) in ConcentrationLineEndpoints(width, height, count, strength))
            {
                context.BeginFigure(inner, false, false);
                context.LineTo(edge, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 집중선을 선마다 개별 Path로 만들어 선 호스트에 채운다.
    // 각 선은 안쪽(중앙 쪽, 투명 1/6) → 바깥(불투명)으로 자기 길이 기준 그라데이션을 갖는다.
    private static void BuildConcentrationLineHost(SpeechBubble bubble)
    {
        var host = bubble.LineHost;
        host.Children.Clear();

        var w = bubble.Container.Width;
        var h = bubble.Container.Height;
        var lineColor = bubble.TextBlock.Fill;

        foreach (var (inner, edge) in ConcentrationLineEndpoints(w, h, bubble.ShapeCount, bubble.ShapeStrength))
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(inner, edge),
                // 안쪽(inner) 투명 → 바깥(edge) 불투명.
                Stroke = CreateDirectionFadeBrush(lineColor, inner, edge),
                StrokeThickness = 1.6,
                IsHitTestVisible = false
            };
            host.Children.Add(path);
        }
    }

    // 속도선(효과선): 한 방향으로 직진하는 일직선 평행선들. 강도 0~100 → 방향 0~360도.
    // 각 선의 길이가 제각각이고 선 사이 간격도 일정하지 않다. 돌기 ×10.
    // (베이스 = -d쪽 시작점, 팁 = 진행 방향 끝점) 목록을 만든다.
    private static List<(Point Base, Point Tip)> EffectLineEndpoints(double width, double height, int count, double strength)
    {
        var t = Math.Clamp(strength, 0, 100) / 100.0;
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
            // 수직 위치(선 간격)를 불규칙하게 흔든다.
            var perp = -half + (i + 0.5) * spacing + (Pseudo(i * 3.1 + 0.7) - 0.5) * spacing * 1.4;
            // 길이를 들쭉날쭉하게: 한쪽 가장자리(-d)에서 시작해 임의 길이만큼 직진한다.
            var length = span * (0.30 + 0.70 * Pseudo(i * 5.3 + 0.9));
            var a0 = -half;
            var a1 = -half + length;
            var baseP = new Point(cx + dx * a0 + px * perp, cy + dy * a0 + py * perp);
            var tip = new Point(cx + dx * a1 + px * perp, cy + dy * a1 + py * perp);
            result.Add((baseP, tip));
        }

        return result;
    }

    // BodyPath.Data용(테두리 없음 판정 등). 실제 그리기는 선마다 BuildEffectLineHost에서 한다.
    private static Geometry CreateEffectLinesGeometry(double width, double height, int count, double strength)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var (baseP, tip) in EffectLineEndpoints(width, height, count, strength))
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

        foreach (var (baseP, tip) in EffectLineEndpoints(w, h, bubble.ShapeCount, bubble.ShapeStrength))
        {
            // 베이스→팁 선분을 박스로 클립해 보이는 구간만 남긴다(c0=베이스쪽, c1=팁쪽).
            if (!ClipSegmentToBox(baseP, tip, w, h, out var c0, out var c1))
            {
                continue;
            }

            var path = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(c0, c1),
                // 팁(c1) 투명 → 베이스(c0) 불투명. 보이는 구간 기준이라 선마다 자기 시작점부터 페이드된다.
                Stroke = CreateDirectionFadeBrush(lineColor, c1, c0),
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
    private static Geometry CreateLobedGeometry(double width, double height, int count, double baseRadiusFactor, double pushFactor, bool angular)
    {
        var n = Math.Max(3, count);
        var cx = width / 2.0;
        var cy = height / 2.0;
        var start = -Math.PI / 2.0;
        var minDist = Math.Min(width, height) * 0.05;

        var points = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var angle = start + i * 2.0 * Math.PI / n;
            // 점마다 반지름을 살짝 흔들어 균일하지 않게 한다.
            var wobble = 0.86 + 0.14 * Math.Abs(Math.Sin(i * 2.3 + 0.6));
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
            var chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var dist = Math.Max(minDist, len + chord * pushFactor);
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
