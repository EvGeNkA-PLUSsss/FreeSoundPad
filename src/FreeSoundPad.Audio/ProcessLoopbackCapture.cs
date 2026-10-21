using System.Runtime.InteropServices;
using FreeSoundPad.Audio.Interop;

namespace FreeSoundPad.Audio;

/// <summary>
/// Captures audio rendered by a single process (and its child processes) using the
/// Windows 10 2004+ WASAPI process loopback API. No system-wide audio leaks into it.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BitsPerSample = 16;

    private readonly uint _processId;
    private readonly SampleRingBuffer[] _targets;
    private readonly AutoResetEvent _sampleEvent = new(false);
    private readonly AutoResetEvent _stopEvent = new(false);

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private volatile bool _stopping;
    private float[] _scratch = new float[8192];

    public ProcessLoopbackCapture(uint processId, params SampleRingBuffer[] targets)
    {
        if (targets.Length == 0) throw new ArgumentException("At least one target buffer is required", nameof(targets));
        _processId = processId;
        _targets = targets;
    }

    public uint ProcessId => _processId;
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<string>? CaptureStopped;

    public bool Start()
    {
        if (IsRunning) return true;
        LastError = null;

        Exception? activationError = null;
        var activationThread = new Thread(() =>
        {
            try
            {
                ActivateAndInitialize();
            }
            catch (Exception ex)
            {
                activationError = ex;
            }
        })
        {
            IsBackground = true,
            Name = "FreeSoundPad process loopback activation",
        };
        activationThread.SetApartmentState(ApartmentState.MTA);
        activationThread.Start();

        if (!activationThread.Join(TimeSpan.FromSeconds(6)))
        {
            LastError = "Не удалось активировать захват звука браузера: превышено время ожидания.";
            return false;
        }

        if (activationError != null)
        {
            LastError = $"Не удалось активировать захват звука браузера: {activationError.Message}";
            return false;
        }

        IsRunning = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "FreeSoundPad process loopback capture",
        };
        _captureThread.Start();
        return true;
    }

    private void ActivateAndInitialize()
    {
        var handler = new ActivationHandler();
        var size = Marshal.SizeOf<AudioClientActivationParams>();
        var blob = Marshal.AllocHGlobal(size);

        try
        {
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = (int)AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = _processId,
                    ProcessLoopbackMode = (int)ProcessLoopbackMode.IncludeTargetProcessTree,
                },
            };
            Marshal.StructureToPtr(activationParams, blob, false);

            var propVariant = new PropVariant
            {
                vt = AudioInterop.VtBlob,
                blobSize = (uint)size,
                blobData = blob,
            };

            var iid = AudioInterop.IIdAudioClient;
            var hr = AudioInterop.ActivateAudioInterfaceAsync(
                AudioInterop.ProcessLoopbackDevicePath,
                ref iid,
                ref propVariant,
                handler,
                out _);

            if (hr != 0) throw Marshal.GetExceptionForHR(hr)!;

            if (!handler.Completed.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("превышено время ожидания активации аудиоинтерфейса");
            }

            if (handler.ActivateResult != 0) throw Marshal.GetExceptionForHR(handler.ActivateResult)!;
            _audioClient = handler.AudioClient ?? throw new InvalidOperationException("аудиоинтерфейс не получен");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }

        // Request 16-bit PCM 48 kHz stereo and let WASAPI convert from the process mix format.
        var format = new WaveFormatEx
        {
            wFormatTag = AudioInterop.WaveFormatPcm,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = Channels * BitsPerSample / 8,
            nAvgBytesPerSec = SampleRate * (uint)(Channels * BitsPerSample / 8),
            cbSize = 0,
        };

        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);

            const uint flags = AudioInterop.StreamFlagsLoopback
                               | AudioInterop.StreamFlagsEventCallback
                               | AudioInterop.StreamFlagsAutoConvertPcm
                               | AudioInterop.StreamFlagsSrcDefaultQuality;

            var hr = _audioClient.Initialize(
                AudioInterop.ShareModeShared,
                flags,
                0,
                0,
                formatPtr,
                IntPtr.Zero);

            if (hr != 0) throw Marshal.GetExceptionForHR(hr)!;
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        var setEventResult = _audioClient.SetEventHandle(_sampleEvent.SafeWaitHandle.DangerousGetHandle());
        if (setEventResult != 0) throw Marshal.GetExceptionForHR(setEventResult)!;

        var captureIid = AudioInterop.IIdAudioCaptureClient;
        var captureHr = _audioClient.GetService(ref captureIid, out var capturePtr);
        if (captureHr != 0) throw Marshal.GetExceptionForHR(captureHr)!;

        try
        {
            _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
        }
        finally
        {
            Marshal.Release(capturePtr);
        }

        var startHr = _audioClient.Start();
        if (startHr != 0) throw Marshal.GetExceptionForHR(startHr)!;
    }

    private void CaptureLoop()
    {
        var handles = new WaitHandle[] { _sampleEvent, _stopEvent };
        var error = "";

        try
        {
            while (!_stopping)
            {
                var signal = WaitHandle.WaitAny(handles, 500);
                if (signal == WaitHandle.WaitTimeout) continue;
                if (signal == 1) break;

                DrainPackets();
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            IsRunning = false;
            if (!_stopping && error.Length == 0) error = "Захват остановлен системой.";
            CaptureStopped?.Invoke(this, error);
        }
    }

    private void DrainPackets()
    {
        if (_captureClient == null) return;

        while (!_stopping)
        {
            var packetHr = _captureClient.GetNextPacketSize(out var framesAvailable);
            if (packetHr != 0) throw Marshal.GetExceptionForHR(packetHr)!;
            if (framesAvailable == 0) break;

            var bufferHr = _captureClient.GetBuffer(
                out var data,
                out var frames,
                out var flags,
                out _,
                out _);
            if (bufferHr != 0) throw Marshal.GetExceptionForHR(bufferHr)!;

            var sampleCount = checked((int)frames * Channels);
            if (sampleCount > 0)
            {
                if (_scratch.Length < sampleCount) _scratch = new float[sampleCount * 2];

                if ((flags & AudioInterop.BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                {
                    Array.Clear(_scratch, 0, sampleCount);
                }
                else
                {
                    unsafe
                    {
                        var source = (short*)data;
                        var target = _scratch;
                        for (var i = 0; i < sampleCount; i++)
                        {
                            target[i] = source[i] / 32768f;
                        }
                    }
                }

                var span = _scratch.AsSpan(0, sampleCount);
                foreach (var target in _targets)
                {
                    target.Write(span);
                }
            }

            var releaseHr = _captureClient.ReleaseBuffer(frames);
            if (releaseHr != 0) throw Marshal.GetExceptionForHR(releaseHr)!;
        }
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        IsRunning = false;
        _stopEvent.Set();

        try
        {
            _audioClient?.Stop();
        }
        catch
        {
            // Device may already be gone.
        }

        try
        {
            _captureThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _captureThread = null;
        _sampleEvent.Set();

        if (_captureClient != null)
        {
            try
            {
                Marshal.ReleaseComObject(_captureClient);
            }
            catch
            {
                // ignore
            }

            _captureClient = null;
        }

        if (_audioClient != null)
        {
            try
            {
                Marshal.ReleaseComObject(_audioClient);
            }
            catch
            {
                // ignore
            }

            _audioClient = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _sampleEvent.Dispose();
        _stopEvent.Dispose();
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEvent Completed = new(false);
        public int ActivateResult = unchecked((int)0x80004005);
        public IAudioClient? AudioClient;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                var hr = operation.GetActivateResult(out var activateResult, out var activatedInterface);
                ActivateResult = hr == 0 ? activateResult : hr;

                if (ActivateResult == 0 && activatedInterface is IAudioClient client)
                {
                    AudioClient = client;
                }
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
            }
            finally
            {
                Completed.Set();
            }

            return 0;
        }
    }
}
