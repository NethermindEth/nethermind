// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public abstract class PacedSweep(IColumnsDb<FlatDbColumns> flat, string markerName, string progressName, int tallyLength, SweepPacer pacer, ILogger logger) : IDisposable
{
    protected const byte Swept = 1;
    protected const byte Unsupported = 2;
    private const int MaxCursorLength = 64;
    private static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);

    private readonly IDb _flatMetadata = flat.GetColumnDb(FlatDbColumns.Metadata);
    private readonly byte[] _markerKey = Keccak.Compute(markerName).BytesToArray();
    private readonly byte[] _progressKey = Keccak.Compute(progressName).BytesToArray();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _loop;
    private byte[]? _checkCursor;
    private bool _checkDone;
    private bool _tallyLoaded;
    private long _lastProgressAt;

    protected readonly long[] Tally = new long[tallyLength];
    protected readonly ILogger Logger = logger;

    protected IColumnsDb<FlatDbColumns> Flat => flat;

    protected abstract string Name(bool repair);

    protected abstract string StartDescription(bool repair);

    protected abstract string Outcome();

    protected abstract string ProgressDetail();

    protected abstract bool FoundOrphans { get; }

    protected abstract bool Scan(bool repair, ReadOnlySpan<byte> start, long maxUnits, TimeSpan budget, out byte[]? next, CancellationToken token);

    protected virtual bool NothingToJudge => !Drained(0);

    protected virtual void OnCompleted()
    {
    }

    public bool AlreadyHandled => _flatMetadata.Get(_markerKey) is [Swept or Unsupported];

    public bool IsSwept => _flatMetadata.Get(_markerKey) is [Swept];

    public void MarkUnsupported() => _flatMetadata.PutSpan(_markerKey, [Unsupported]);

    public void Start(bool repair, ulong drainedAtBlock)
    {
        if (_loop is not null) return;

        _loop = new Thread(() => RunLoop(repair, drainedAtBlock)) { IsBackground = true, Name = Name(repair) };
        _loop.Start();
    }

    internal void RunToCompletion(bool repair, CancellationToken token)
    {
        while (!RunOnePass(repair, long.MaxValue, TimeSpan.MaxValue, token))
        {
        }
    }

    internal bool RunOnePass(bool repair, long maxUnits, TimeSpan budget, CancellationToken token)
    {
        bool resuming;
        byte[]? start;
        if (repair) resuming = TryReadProgress(out start);
        else
        {
            if (_checkDone) return true;

            start = _checkCursor;
            resuming = start is not null;
        }

        if (!resuming) Array.Clear(Tally);
        _tallyLoaded = true;

        bool completed = Scan(repair, start, maxUnits, budget, out byte[]? next, token);
        if (repair)
        {
            if (completed)
            {
                OnCompleted();
                StampSwept();
            }
            else WriteProgress(next!);
        }
        else
        {
            _checkCursor = next;
            _checkDone = completed;
        }

        return completed;
    }

    internal bool TryStampFresh()
    {
        if (!NothingToJudge) return false;

        StampSwept();
        return true;
    }

    protected bool Drained(ulong drainedAtBlock)
    {
        StateId persisted = BasePersistence.ReadCurrentState(_flatMetadata);
        return persisted != StateId.PreGenesis && persisted != StateId.Sync && persisted.BlockNumber >= drainedAtBlock;
    }

    protected void StampSwept()
    {
        using IColumnsWriteBatch<FlatDbColumns> stamp = flat.StartWriteBatch();
        IWriteBatch metadata = stamp.GetColumnBatch(FlatDbColumns.Metadata);
        metadata.Remove(_progressKey);
        metadata.PutSpan(_markerKey, [Swept]);
    }

    private void RunLoop(bool repair, ulong drainedAtBlock)
    {
        CancellationToken token = _cts.Token;
        try
        {
            if (repair && TryStampFresh())
            {
                if (Logger.IsInfo) Logger.Info($"{Name(repair)} skipped: this database has no persisted block state, so it holds nothing to judge and what a state sync writes next is not orphaned; recorded as swept.");
                return;
            }

            if (!Drained(drainedAtBlock))
            {
                if (Logger.IsInfo) Logger.Info($"{Name(repair)} waits for the persisted flat state to reach block {drainedAtBlock}, so that everything the previous binary left queued has landed first.");
                while (!Drained(drainedAtBlock))
                {
                    if (token.WaitHandle.WaitOne(DrainPoll)) return;
                }
            }

            if (Logger.IsInfo) Logger.Info(StartDescription(repair));
            long startedAt = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                if (pacer.Run(() => RunOnePass(repair, long.MaxValue, PassBudget, token), token)) break;
            }

            if (token.IsCancellationRequested) return;

            string outcome = $"{Outcome()}; this start took {Stopwatch.GetElapsedTime(startedAt)}.";
            if (repair)
            {
                if (Logger.IsInfo) Logger.Info($"{Name(repair)} done, orphaned entries deleted: {outcome}");
            }
            else if (!Drained(0))
            {
                if (Logger.IsWarn) Logger.Warn($"{Name(repair)} inconclusive: the flat database was cleared under it by a state sync, and slots land before their accounts while that sync writes. {outcome}");
            }
            else if (FoundOrphans)
            {
                if (Logger.IsWarn) Logger.Warn($"{Name(repair)} FAILED: {outcome} Set FlatDb.SweepOrphanStorage to delete them.");
            }
            else if (Logger.IsInfo) Logger.Info($"{Name(repair)} passed: {outcome}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (Logger.IsError) Logger.Error($"{Name(repair)} failed; it resumes from its cursor on the next start.", e);
        }
    }

    protected void LogProgress(bool repair, ReadOnlySpan<byte> cursor)
    {
        if (!Logger.IsInfo) return;
        if (_lastProgressAt != 0 && Stopwatch.GetElapsedTime(_lastProgressAt) < ProgressInterval) return;

        _lastProgressAt = Stopwatch.GetTimestamp();
        Logger.Info($"{Name(repair)} at {Position(cursor):P1} of the key space: {ProgressDetail()}.");
    }

    protected static double Position(ReadOnlySpan<byte> cursor) => cursor.Length >= sizeof(uint) ? BinaryPrimitives.ReadUInt32BigEndian(cursor) / (double)(1L << 32) : 0;

    private bool TryReadProgress(out byte[]? cursor)
    {
        cursor = null;
        byte[]? value = _flatMetadata.Get(_progressKey);
        if (value is null || value.Length < 1 + Tally.Length * sizeof(long)) return false;

        int cursorLength = value[0];
        if (cursorLength > MaxCursorLength || value.Length != 1 + cursorLength + Tally.Length * sizeof(long)) return false;

        cursor = value.AsSpan(1, cursorLength).ToArray();
        if (!_tallyLoaded)
        {
            ReadOnlySpan<byte> tally = value.AsSpan(1 + cursorLength);
            for (int i = 0; i < Tally.Length; i++) Tally[i] = BinaryPrimitives.ReadInt64BigEndian(tally[(i * sizeof(long))..]);
        }

        return true;
    }

    protected void WriteProgress(ReadOnlySpan<byte> cursor)
    {
        if (cursor.Length > MaxCursorLength) throw new ArgumentOutOfRangeException(nameof(cursor));

        Span<byte> value = stackalloc byte[1 + MaxCursorLength + Tally.Length * sizeof(long)];
        value[0] = (byte)cursor.Length;
        cursor.CopyTo(value[1..]);
        Span<byte> tally = value[(1 + cursor.Length)..];
        for (int i = 0; i < Tally.Length; i++) BinaryPrimitives.WriteInt64BigEndian(tally[(i * sizeof(long))..], Tally[i]);
        _flatMetadata.PutSpan(_progressKey, value[..(1 + cursor.Length + Tally.Length * sizeof(long))]);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _loop?.Join();
        _cts.Dispose();
    }
}
