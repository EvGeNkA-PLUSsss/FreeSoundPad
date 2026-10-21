using System.Runtime.InteropServices;

namespace FreeSoundPad.Audio.Interop;

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    IncludeTargetProcessTree = 0,
    ExcludeTargetProcessTree = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

/// <summary>
/// PROPVARIANT with an inline BLOB (VT_BLOB). Size matches the native 64-bit PROPVARIANT (24 bytes).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public uint blobSize;
    [FieldOffset(16)] public IntPtr blobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(
        out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        int shareMode,
        uint streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr format,
        IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint padding);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, out IntPtr service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr data,
        out uint numFrames,
        out uint flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFrames);

    [PreserveSig]
    int GetNextPacketSize(out uint numFrames);
}

internal static class AudioInterop
{
    public const string ProcessLoopbackDevicePath = "VAD\\Process_Loopback";

    public static readonly Guid IIdAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IIdAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public const int ShareModeShared = 0;

    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    public const uint BufferFlagsSilent = 0x00000002;

    public const ushort WaveFormatPcm = 1;
    public const ushort VtBlob = 65;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
