using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KomaForge;

// Windows Media Foundation SourceReader로 동영상의 임의 시각 프레임을 '프레임 정확'하게 디코드한다.
// OS 내장 디코더만 P/Invoke로 호출하므로 외부/네이티브 DLL이 필요 없고 단일 exe가 유지된다.
// 내보내기 프레임 추출 전용(라이브 재생은 그대로 MediaElement 사용). 실패 시 호출부가 MediaPlayer로 폴백한다.
public sealed class VideoFrameReader : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    private IMFSourceReader? _reader;
    private readonly int _stride; // 음수면 bottom-up.
    private bool _disposed;

    private VideoFrameReader(IMFSourceReader reader, int width, int height, int stride)
    {
        _reader = reader;
        Width = width;
        Height = height;
        _stride = stride;
    }

    // 동영상을 열어 RGB32 출력으로 설정한다. 실패(코덱 미지원 등)하면 null.
    public static VideoFrameReader? TryCreate(string path)
    {
        var started = false;
        try
        {
            if (MFStartup(MF_VERSION, MFSTARTUP_LITE) < 0)
            {
                return null;
            }
            started = true;

            if (MFCreateAttributes(out var attrs, 1) < 0 || attrs == null)
            {
                MFShutdown();
                return null;
            }
            var enableProc = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            attrs.SetUINT32(ref enableProc, 1); // 디코더가 RGB32로 변환하도록 허용.

            if (MFCreateSourceReaderFromURL(path, attrs, out var reader) < 0 || reader == null)
            {
                MFShutdown();
                return null;
            }

            // 모든 스트림 해제 후 첫 비디오 스트림만 선택.
            reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

            // 출력 형식을 RGB32로 지정.
            if (MFCreateMediaType(out var mt) < 0 || mt == null)
            {
                MFShutdown();
                return null;
            }
            var major = MF_MT_MAJOR_TYPE;
            var video = MFMediaType_Video;
            var subtype = MF_MT_SUBTYPE;
            var rgb32 = MFVideoFormat_RGB32;
            mt.SetGUID(ref major, ref video);
            mt.SetGUID(ref subtype, ref rgb32);
            if (reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, mt) < 0)
            {
                MFShutdown();
                return null;
            }

            // 협상된 실제 출력 타입에서 프레임 크기·스트라이드를 읽는다.
            if (reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var actual) < 0 || actual == null)
            {
                MFShutdown();
                return null;
            }
            var sizeKey = MF_MT_FRAME_SIZE;
            if (actual.GetUINT64(ref sizeKey, out var packed) < 0)
            {
                MFShutdown();
                return null;
            }
            var width = (int)(packed >> 32);
            var height = (int)(packed & 0xFFFFFFFF);

            var strideKey = MF_MT_DEFAULT_STRIDE;
            int stride = actual.GetUINT32(ref strideKey, out var strideU) >= 0 ? unchecked((int)strideU) : width * 4;
            if (stride == 0)
            {
                stride = width * 4;
            }

            if (width <= 0 || height <= 0)
            {
                MFShutdown();
                return null;
            }

            return new VideoFrameReader(reader, width, height, stride);
        }
        catch
        {
            if (started)
            {
                try { MFShutdown(); } catch { /* 무시 */ }
            }
            return null;
        }
    }

    // 시각(ms)의 프레임을 디코드해 WPF 비트맵으로 반환한다. 실패하면 null.
    public BitmapSource? GetFrame(double timeMs)
    {
        var reader = _reader;
        if (reader == null)
        {
            return null;
        }

        try
        {
            var target = (long)(timeMs * 10000.0); // 100ns 단위.
            var format = Guid.Empty;
            var pos = new PROPVARIANT { vt = VT_I8, llVal = target };
            reader.SetCurrentPosition(ref format, ref pos);

            IMFSample? sample = null;
            for (var guard = 0; guard < 2000; guard++)
            {
                if (reader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                        out _, out var flags, out var ts, out var s) < 0)
                {
                    return null;
                }

                if ((flags & MF_SOURCEREADERF_ENDOFSTREAM) != 0)
                {
                    sample = s; // 마지막 샘플(있으면) 사용.
                    break;
                }

                if (s == null)
                {
                    continue; // 스트림 틱/형식 변경 등.
                }

                if (ts >= target)
                {
                    sample = s; // 목표 시각에 도달한 프레임.
                    break;
                }

                Marshal.ReleaseComObject(s); // 목표 이전 프레임은 버리고 계속 디코드.
            }

            if (sample == null)
            {
                return null;
            }

            try
            {
                if (sample.ConvertToContiguousBuffer(out var buffer) < 0 || buffer == null)
                {
                    return null;
                }

                try
                {
                    if (buffer.Lock(out var ptr, out _, out _) < 0 || ptr == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        var absStride = Math.Abs(_stride);
                        var rowBytes = Width * 4;
                        var pixels = new byte[Height * rowBytes];
                        for (var y = 0; y < Height; y++)
                        {
                            var srcRow = _stride >= 0 ? y : Height - 1 - y; // bottom-up면 행 뒤집기.
                            Marshal.Copy(ptr + srcRow * absStride, pixels, y * rowBytes, rowBytes);
                        }

                        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, rowBytes);
                        bmp.Freeze();
                        return bmp;
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_reader != null)
        {
            try { Marshal.ReleaseComObject(_reader); } catch { /* 무시 */ }
            _reader = null;
            try { MFShutdown(); } catch { /* 무시 */ }
        }
    }

    // === Media Foundation P/Invoke ===

    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_LITE = 0x1;
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCEREADERF_ENDOFSTREAM = 0x00000002;
    private const ushort VT_I8 = 20;

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public long llVal;
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType ppMFType);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string pwszURL, IMFAttributes pAttributes, out IMFSourceReader ppSourceReader);

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out bool result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out bool result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, uint size, IntPtr length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint bufSize, IntPtr blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buf, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, IntPtr unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
        [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
        [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, ref PROPVARIANT varPosition);
        [PreserveSig] int ReadSample(uint dwStreamIndex, uint dwControlFlags, out uint pdwActualStreamIndex,
            out uint pdwStreamFlags, out long pllTimestamp, out IMFSample? ppSample);
    }

    // 상속 없이 전체 슬롯을 직접 선언(C# COM 상속의 vtable 순서 문제 회피).
    // 0~29: IMFAttributes 슬롯(호출 안 함 → 자리만), 30~38: IMFSample 메서드.
    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int _s0(); [PreserveSig] int _s1(); [PreserveSig] int _s2(); [PreserveSig] int _s3();
        [PreserveSig] int _s4(); [PreserveSig] int _s5(); [PreserveSig] int _s6(); [PreserveSig] int _s7();
        [PreserveSig] int _s8(); [PreserveSig] int _s9(); [PreserveSig] int _s10(); [PreserveSig] int _s11();
        [PreserveSig] int _s12(); [PreserveSig] int _s13(); [PreserveSig] int _s14(); [PreserveSig] int _s15();
        [PreserveSig] int _s16(); [PreserveSig] int _s17(); [PreserveSig] int _s18(); [PreserveSig] int _s19();
        [PreserveSig] int _s20(); [PreserveSig] int _s21(); [PreserveSig] int _s22(); [PreserveSig] int _s23();
        [PreserveSig] int _s24(); [PreserveSig] int _s25(); [PreserveSig] int _s26(); [PreserveSig] int _s27();
        [PreserveSig] int _s28(); [PreserveSig] int _s29(); // ← IMFAttributes 30슬롯
        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);          // 30
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);               // 31
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);           // 32
        [PreserveSig] int SetSampleTime(long hnsSampleTime);                // 33
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);   // 34
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);        // 35
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);          // 36
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer); // 37
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);      // 38
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    }
}
