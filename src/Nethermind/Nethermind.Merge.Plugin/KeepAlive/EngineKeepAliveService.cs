// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Api;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.KeepAlive;

/// <summary>
/// Keeps the database device and a CPU core out of their deep idle states between Engine API calls, and measures
/// how long that takes them, so that a slow first read of a block can be told apart from the block itself.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated background thread ticks every <see cref="IMergeConfig.EngineKeepAliveIntervalMs"/>, and at once when
/// <see cref="Signal"/> is called (by <c>engine_getBlobs</c> and <c>engine_forkchoiceUpdated</c>, the requests a
/// consensus client sends ahead of <c>engine_newPayload</c>). In <see cref="EngineKeepAliveMode.Disk"/> a tick
/// reads 4 KiB of a dedicated file in the database directory past the page cache (<see cref="DiskReadProbe"/>);
/// in <see cref="EngineKeepAliveMode.Cpu"/> it spins for <see cref="SpinMicroseconds"/>. Every tick the timer woke
/// records its wake-up jitter: how late after the requested time the thread ran.
/// </para>
/// <para>
/// Nothing here is on the block-processing path or has a consensus effect. The callers only set an event; the file
/// is created and opened on the keep-alive's own thread. Every failure is caught and logged; the disk keep-alive
/// switches itself off after <see cref="MaxConsecutiveReadErrors"/> failed reads in a row.
/// </para>
/// </remarks>
public sealed class EngineKeepAliveService : IDisposable
{
    internal const string FileName = "engine-keepalive.dat";
    internal const int SpinMicroseconds = 20;
    internal const int MaxConsecutiveReadErrors = 10;
    internal static readonly long SlowReadTicks = Stopwatch.Frequency / 1000;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly EngineKeepAliveMode _mode;
    private readonly int _intervalMs;
    private readonly string _filePath;
    private readonly int _fileSize;
    private readonly bool _diskSupported;
    private readonly bool _allowDirect;
    private readonly ILogger _logger;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private int _started;
    private volatile bool _disposed;

    private long _ticks;
    private long _signalledTicks;
    private long _cpuTicks;
    private long _reads;
    private long _readErrors;
    private long _lastReadMicros;
    private long _maxReadMicros;
    private long _lastJitterMicros;
    private long _maxJitterMicros;
    private int _diskReadMode;
    private int _consecutiveReadErrors;

    public EngineKeepAliveService(IMergeConfig mergeConfig, IInitConfig initConfig, ILogManager logManager)
        : this(
            mergeConfig.EngineKeepAlive,
            mergeConfig.EngineKeepAliveIntervalMs,
            FileName.GetApplicationResourcePath(initConfig.BaseDbPath),
            OperatingSystem.IsLinux() && Environment.Is64BitProcess,
            allowDirect: true,
            DiskReadProbe.DefaultFileSize,
            logManager)
    {
    }

    internal EngineKeepAliveService(
        EngineKeepAliveMode mode,
        int intervalMs,
        string filePath,
        bool diskSupported,
        bool allowDirect,
        int fileSize,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<EngineKeepAliveService>();
        _mode = mode;
        _intervalMs = intervalMs;
        if (_intervalMs < 1)
        {
            if (mode != EngineKeepAliveMode.Off && _logger.IsWarn) _logger.Warn($"Engine keep-alive interval {intervalMs} ms is below 1 ms; using 1 ms.");
            _intervalMs = 1;
        }

        _filePath = filePath;
        _fileSize = fileSize;
        _diskSupported = diskSupported;
        _allowDirect = allowDirect;
    }

    public EngineKeepAliveMode Mode => _mode;
    public bool IsEnabled => _mode != EngineKeepAliveMode.Off;
    public bool IsStarted => Volatile.Read(ref _started) != 0;
    public bool IsRunning => _thread?.IsAlive == true;

    internal long Ticks => Interlocked.Read(ref _ticks);
    internal long SignalledTicks => Interlocked.Read(ref _signalledTicks);
    internal long CpuTicks => Interlocked.Read(ref _cpuTicks);
    internal long Reads => Interlocked.Read(ref _reads);
    internal long ReadErrors => Interlocked.Read(ref _readErrors);
    internal long LastReadMicros => Interlocked.Read(ref _lastReadMicros);
    internal long MaxReadMicros => Interlocked.Read(ref _maxReadMicros);
    internal long LastJitterMicros => Interlocked.Read(ref _lastJitterMicros);
    internal long MaxJitterMicros => Interlocked.Read(ref _maxJitterMicros);
    internal DiskKeepAliveReadMode DiskReadMode => (DiskKeepAliveReadMode)Volatile.Read(ref _diskReadMode);

    /// <summary>Starts the keep-alive's thread, once; does nothing when it is off, started or disposed.</summary>
    /// <remarks>Does no I/O: the file is created and opened on the keep-alive's thread.</remarks>
    public void Start()
    {
        if (!IsEnabled || _disposed || Interlocked.Exchange(ref _started, 1) != 0) return;

        try
        {
            Thread thread = new(Run) { IsBackground = true, Name = "Engine keep-alive" };
            _thread = thread;
            thread.Start();
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Engine keep-alive could not start its thread.", e);
        }
    }

    /// <summary>Asks for a tick now. Never blocks and never throws; does nothing before <see cref="Start"/>.</summary>
    public void Signal()
    {
        if (_disposed || Volatile.Read(ref _started) == 0) return;

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Run()
    {
        DiskReadProbe? disk = null;
        try
        {
            if ((_mode & EngineKeepAliveMode.Disk) != 0) disk = OpenDisk();
            bool cpu = (_mode & EngineKeepAliveMode.Cpu) != 0;

            if (_logger.IsInfo) _logger.Info($"Engine keep-alive started: mode {_mode}, interval {_intervalMs} ms, disk reads {DiskReadMode}{(disk is null ? "" : $" from {disk.Path}")}.");

            long intervalTicks = _intervalMs * Stopwatch.Frequency / 1000;
            long lastRead = 0;
            while (!_disposed)
            {
                long waitStart = Stopwatch.GetTimestamp();
                bool signalled = _wake.WaitOne(_intervalMs);
                long woke = Stopwatch.GetTimestamp();
                if (_disposed) break;

                try
                {
                    Tick(ref disk, cpu, signalled, signalled ? -1 : woke - waitStart - intervalTicks, ref lastRead);
                }
                catch (Exception e)
                {
                    if (_logger.IsWarn) _logger.Warn($"Engine keep-alive tick failed: {e}");
                }
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Disposed while the thread was starting.
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Engine keep-alive stopped.", e);
        }
        finally
        {
            disk?.Dispose();
            Volatile.Write(ref _diskReadMode, (int)DiskKeepAliveReadMode.None);
            Metrics.EngineKeepAliveDiskReadMode = (int)DiskKeepAliveReadMode.None;
        }
    }

    private DiskReadProbe? OpenDisk()
    {
        if (!_diskSupported)
        {
            if (_logger.IsInfo) _logger.Info("Engine keep-alive: the disk keep-alive needs 64-bit Linux; it is off here.");
            return null;
        }

        try
        {
            DiskReadProbe disk = DiskReadProbe.Open(_filePath, _fileSize, _allowDirect);
            SetDiskReadMode(disk.Mode);
            if (disk.Mode != DiskKeepAliveReadMode.Direct && _logger.IsInfo)
                _logger.Info($"Engine keep-alive: O_DIRECT is not available for {_filePath}; reading after dropping the file from the page cache.");
            return disk;
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Engine keep-alive: the disk keep-alive is off, {_filePath} could not be prepared: {e.Message}");
            return null;
        }
    }

    private void Tick(ref DiskReadProbe? disk, bool cpu, bool signalled, long jitterTicks, ref long lastRead)
    {
        Interlocked.Increment(ref _ticks);
        Metrics.EngineKeepAliveTicks++;
        if (signalled)
        {
            Interlocked.Increment(ref _signalledTicks);
            Metrics.EngineKeepAliveSignalledTicks++;
        }
        else
        {
            long jitterMicros = Math.Max(0, jitterTicks) * 1_000_000 / Stopwatch.Frequency;
            Interlocked.Exchange(ref _lastJitterMicros, jitterMicros);
            Metrics.EngineKeepAliveLastJitterMicros = jitterMicros;
            if (jitterMicros > _maxJitterMicros)
            {
                Interlocked.Exchange(ref _maxJitterMicros, jitterMicros);
                Metrics.EngineKeepAliveMaxJitterMicros = jitterMicros;
            }

            Metrics.EngineKeepAliveJitterMicros.Observe(jitterMicros);
        }

        if (disk is not null) ReadBlock(ref disk, signalled, ref lastRead);

        if (cpu)
        {
            Spin();
            Interlocked.Increment(ref _cpuTicks);
            Metrics.EngineKeepAliveCpuTicks++;
        }
    }

    private void ReadBlock(ref DiskReadProbe? disk, bool signalled, ref long lastRead)
    {
        long now = Stopwatch.GetTimestamp();
        long sincePreviousMs = lastRead == 0 ? -1 : (now - lastRead) * 1000 / Stopwatch.Frequency;
        DiskKeepAliveReadMode modeBefore = disk!.Mode;
        try
        {
            long elapsed = disk.Read();
            lastRead = Stopwatch.GetTimestamp();
            if (disk.Mode != modeBefore)
            {
                SetDiskReadMode(disk.Mode);
                if (_logger.IsInfo) _logger.Info($"Engine keep-alive: O_DIRECT reads of {disk.Path} were refused; reading after dropping the file from the page cache.");
            }

            long micros = elapsed * 1_000_000 / Stopwatch.Frequency;
            Interlocked.Increment(ref _reads);
            Metrics.EngineKeepAliveReads++;
            Interlocked.Exchange(ref _lastReadMicros, micros);
            Metrics.EngineKeepAliveLastReadMicros = micros;
            if (micros > _maxReadMicros)
            {
                Interlocked.Exchange(ref _maxReadMicros, micros);
                Metrics.EngineKeepAliveMaxReadMicros = micros;
            }

            Metrics.EngineKeepAliveReadMicros.Observe(micros);
            if (elapsed > SlowReadTicks)
            {
                Metrics.EngineKeepAliveSlowReads++;
                if (_logger.IsDebug) _logger.Debug($"Engine keep-alive read took {micros} us ({(signalled ? "signalled" : "timer")} tick, {(sincePreviousMs < 0 ? "the first read" : $"{sincePreviousMs} ms after the previous read")}).");
            }

            _consecutiveReadErrors = 0;
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _readErrors);
            Metrics.EngineKeepAliveReadErrors++;
            if (++_consecutiveReadErrors >= MaxConsecutiveReadErrors)
            {
                if (_logger.IsWarn) _logger.Warn($"Engine keep-alive: the disk keep-alive is off after {_consecutiveReadErrors} failed reads in a row: {e.Message}");
                disk.Dispose();
                disk = null;
                SetDiskReadMode(DiskKeepAliveReadMode.None);
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug($"Engine keep-alive read failed: {e.Message}");
            }
        }
    }

    private void SetDiskReadMode(DiskKeepAliveReadMode mode)
    {
        Volatile.Write(ref _diskReadMode, (int)mode);
        Metrics.EngineKeepAliveDiskReadMode = (int)mode;
    }

    private static void Spin()
    {
        long until = Stopwatch.GetTimestamp() + SpinMicroseconds * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(8);
    }

    /// <summary>Stops the thread; waits for it for up to two seconds.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Thread? thread = _thread;
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        if (thread is null || thread.Join(JoinTimeout))
        {
            _wake.Dispose();
        }
        else if (_logger.IsWarn)
        {
            // Left to the thread: a read stuck in the kernel still owns the probe and the event.
            _logger.Warn("Engine keep-alive thread did not stop within 2 s.");
        }
    }
}
