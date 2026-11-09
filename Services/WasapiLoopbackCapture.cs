using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoEditor.Services;

/// <summary>Small native WASAPI loopback capture. It records the default Windows render mix without a driver or package.</summary>
internal sealed class WasapiLoopbackCapture : IDisposable
{
    const uint LoopbackFlag = 0x00020000;
    const uint SilentFlag = 0x00000002;
    const int ClassContextAll = 23;
    const int ShareModeShared = 0;
    static readonly Guid DeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid DeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    volatile bool _running;
    Task? _worker;
    Exception? _failure;

    public async Task StartAsync(string outputPath)
    {
        if (_worker != null) throw new InvalidOperationException("WASAPI capture is already running.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
        _worker = Task.Run(() => CaptureWorker(outputPath, ready));
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch
        {
            _running = false;
            try { await _worker; } catch { }
            _worker = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        _running = false;
        if (_worker == null) return;
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { _worker = null; }
        if (_failure != null) throw new InvalidOperationException("WASAPI loopback capture failed.", _failure);
    }

    void CaptureWorker(string outputPath, TaskCompletionSource ready)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr mixFormat = IntPtr.Zero;
        bool comInitialized = false;
        try
        {
            int init = CoInitializeEx(IntPtr.Zero, 0);
            if (init < 0) Marshal.ThrowExceptionForHR(init);
            comInitialized = true;

            var clsid = DeviceEnumeratorClsid;
            var iid = DeviceEnumeratorIid;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, ClassContextAll, ref iid, out object enumObject));
            enumerator = (IMMDeviceEnumerator)enumObject;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
            var audioIid = AudioClientIid;
            Marshal.ThrowExceptionForHR(device.Activate(ref audioIid, ClassContextAll, IntPtr.Zero, out object clientObject));
            audioClient = (IAudioClient)clientObject;
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out mixFormat));
            var format = Marshal.PtrToStructure<WaveFormatEx>(mixFormat);
            int formatTag = format.FormatTag;
            if (formatTag == 0xFFFE)
            {
                var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(mixFormat, 24));
                formatTag = subFormat == FloatSubFormat ? 3 : subFormat == PcmSubFormat ? 1 : 0;
            }
            if (formatTag is not (1 or 3) || format.Channels is 0 or > 32 || format.BlockAlign == 0)
                throw new NotSupportedException("The selected Windows playback device uses an unsupported audio format.");

            var session = Guid.Empty;
            Marshal.ThrowExceptionForHR(audioClient.Initialize(ShareModeShared, LoopbackFlag, 10_000_000, 0, mixFormat, ref session));
            var captureIid = AudioCaptureClientIid;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref captureIid, out object captureObject));
            captureClient = (IAudioCaptureClient)captureObject;
            Marshal.ThrowExceptionForHR(audioClient.Start());

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            WriteWaveHeader(writer, formatTag, format.Channels, format.SamplesPerSec, format.BitsPerSample, 0);
            stream.Flush();
            ready.TrySetResult();

            long dataBytes = 0;
            var flushClock = Stopwatch.StartNew();
            byte[]? managedBuffer = null;
            while (_running)
            {
                Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint packetFrames));
                if (packetFrames == 0) { Thread.Sleep(5); continue; }

                while (packetFrames > 0 && _running)
                {
                    Marshal.ThrowExceptionForHR(captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int bytes = checked((int)(frames * format.BlockAlign));
                    try
                    {
                        if ((flags & SilentFlag) != 0)
                        {
                            managedBuffer ??= new byte[Math.Max(bytes, 64 * 1024)];
                            if (managedBuffer.Length < bytes) managedBuffer = new byte[bytes];
                            Array.Clear(managedBuffer, 0, bytes);
                            stream.Write(managedBuffer, 0, bytes);
                        }
                        else
                        {
                            if (managedBuffer == null || managedBuffer.Length < bytes) managedBuffer = new byte[bytes];
                            Marshal.Copy(data, managedBuffer, 0, bytes);
                            stream.Write(managedBuffer, 0, bytes);
                        }
                        dataBytes += bytes;
                    }
                    finally { Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames)); }
                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
                }

                if (flushClock.ElapsedMilliseconds >= 1000)
                {
                    WriteWaveHeader(writer, formatTag, format.Channels, format.SamplesPerSec, format.BitsPerSample, dataBytes);
                    stream.Flush();
                    flushClock.Restart();
                }
            }
            Marshal.ThrowExceptionForHR(audioClient.Stop());
            WriteWaveHeader(writer, formatTag, format.Channels, format.SamplesPerSec, format.BitsPerSample, dataBytes);
            stream.Flush(true);
        }
        catch (Exception ex)
        {
            _failure = ex;
            ready.TrySetException(ex);
        }
        finally
        {
            if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
            Release(captureClient); Release(audioClient); Release(device); Release(enumerator);
            if (comInitialized) CoUninitialize();
        }
    }

    static void WriteWaveHeader(BinaryWriter writer, int formatTag, ushort channels, uint sampleRate, ushort bitsPerSample, long dataBytes)
    {
        long position = writer.BaseStream.Position;
        writer.BaseStream.Position = 0;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16u); writer.Write((ushort)formatTag);
        writer.Write(channels); writer.Write(sampleRate); ushort blockAlign = (ushort)(channels * bitsPerSample / 8);
        writer.Write(sampleRate * blockAlign); writer.Write(blockAlign); writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((uint)Math.Min(uint.MaxValue, dataBytes));
        writer.BaseStream.Position = position;
    }

    static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    public void Dispose()
    {
        _running = false;
        if (_worker != null) try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _worker = null;
    }

    [StructLayout(LayoutKind.Sequential)] struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [DllImport("ole32.dll", ExactSpelling = true)] static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}
