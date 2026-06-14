using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace KomaForge;

// 움직이는 gif/webp/apng를 '재생하며 프레임마다' 디코드하는 스트리밍 플레이어.
// 전 프레임을 미리 펼치지 않으므로 메모리는 현재+직전 프레임 수준(동영상과 유사)이고,
// 첫 진입 시의 일괄 디코드 멈춤도 없다(프레임당 소량 CPU로 분산).
public sealed class AnimatedPlayer : IDisposable
{
    private SKCodec? _codec;
    private readonly SKImageInfo _info;
    private readonly SKCodecFrameInfo[] _frameInfos;
    private SKBitmap? _buffer;     // 직전에 디코드한 프레임 픽셀(델타 합성의 기반).
    private int _bufferIndex = -1; // _buffer에 들어 있는 프레임 번호.

    public int FrameCount { get; }

    private AnimatedPlayer(SKCodec codec)
    {
        _codec = codec;
        FrameCount = codec.FrameCount;
        _frameInfos = codec.FrameInfo;
        _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
    }

    // 움직이는(2프레임 이상) 이미지면 플레이어를 만든다. 정지/지원불가/실패면 null.
    public static AnimatedPlayer? TryCreate(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".gif" or ".webp" or ".png" or ".apng"))
        {
            return null;
        }

        SKCodec? codec = null;
        try
        {
            codec = SKCodec.Create(path);
            if (codec == null || codec.FrameCount <= 1)
            {
                codec?.Dispose();
                return null;
            }

            return new AnimatedPlayer(codec);
        }
        catch
        {
            codec?.Dispose();
            return null;
        }
    }

    // 프레임별 표시 시간(ms). 값이 없으면 100ms로 본다.
    public int DelayMs(int index)
    {
        if (index < 0 || index >= _frameInfos.Length)
        {
            return 100;
        }

        var duration = _frameInfos[index].Duration;
        return duration > 0 ? duration : 100;
    }

    // index 프레임을 디코드해 WPF 비트맵으로 반환한다. 순차 재생(0,1,…,N-1,0,…)을 전제로,
    // 델타 프레임이면 직전 프레임 픽셀 위에 변화분을 합성한다.
    public BitmapSource DecodeFrame(int index)
    {
        var codec = _codec ?? throw new ObjectDisposedException(nameof(AnimatedPlayer));

        var fresh = new SKBitmap(_info);
        SKCodecOptions options;
        if (index >= 0 && index < _frameInfos.Length
            && _frameInfos[index].RequiredFrame != -1
            && _buffer != null && _bufferIndex == index - 1)
        {
            _buffer.CopyTo(fresh);                          // 직전 프레임 위에 합성한다(버퍼엔 index-1이 들어있음).
            options = new SKCodecOptions(index, index - 1);
        }
        else
        {
            options = new SKCodecOptions(index);            // 독립(키)프레임으로 디코드.
        }

        codec.GetPixels(_info, fresh.GetPixels(), options);

        var wpf = BitmapSource.Create(_info.Width, _info.Height, 96, 96,
            PixelFormats.Pbgra32, null, fresh.Bytes, _info.RowBytes);
        wpf.Freeze();

        _buffer?.Dispose();
        _buffer = fresh;
        _bufferIndex = index;
        return wpf;
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
        _codec?.Dispose();
        _codec = null;
    }
}
