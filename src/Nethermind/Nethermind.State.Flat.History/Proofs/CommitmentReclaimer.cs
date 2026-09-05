// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentReclaimer(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata, ArchiveProofSettings settings, ILogManager logManager) : IDisposable
{
    private const int NodesPerChunk = 4096;
    private const int WritesPerBatch = 1024;
    private const int KeyOverhead = CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength + CommitmentKeyLayout.SuffixLength;

    private readonly CommitmentStore _accounts = new(history.GetColumnDb(FlatHistoryColumns.AccountCommitments), policy, 0);
    private readonly CommitmentStore _storages = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments), policy, CommitmentKeyLayout.IdentityLength);
    private readonly ILogger _logger = logManager.GetClassLogger<CommitmentReclaimer>();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _loop;
    private bool _started;
    private int _disposed;

    public bool Enabled => settings.RecentEpochs > 0 || settings.FineEpochs > 0;

    internal event Action? PassCompleted;

    public void Start()
    {
        if (!Enabled || _started) return;

        _started = true;
        _loop = new Thread(RunLoop) { IsBackground = true, Name = "Archive proof commitment reclaimer" };
        _loop.Start();
        Wake();
    }

    public void PruneBelow(ulong headBlock)
    {
        if (!Enabled) return;

        ulong headEpoch = policy.Epoch(headBlock);
        if (settings.FineEpochs > 0 && TryFloor(headEpoch, settings.FineEpochs, out ulong fineFrom) && metadata.TryRaiseFineFromEpoch(fineFrom) && _logger.IsInfo)
        {
            _logger.Info(
                $"Archive proof commitments below epoch {fineFrom} (block {policy.EpochStart(fineFrom)}) are losing their per-block rows; proofs there are still served, rebuilt from the checkpoint rows, which costs a second or so instead of a hundred milliseconds.");
        }

        if (settings.RecentEpochs > 0 && TryFloor(headEpoch, settings.RecentEpochs, out ulong retainedFrom) && metadata.TryRaiseRetainedFromEpoch(retainedFrom))
        {
            metadata.TryRaiseFineFromEpoch(retainedFrom);
            if (_logger.IsInfo) _logger.Info(
                $"Archive proof commitments below epoch {retainedFrom} (block {policy.EpochStart(retainedFrom)}) are no longer served; historical proofs start at that block, keeping the {settings.RecentEpochs} most recent epochs of 2^{policy.EpochLog2} blocks. Their rows are carried forward and reclaimed in the background.");
        }

        Wake();
    }

    internal void ReclaimNow(CancellationToken token)
    {
        while (RunOnePass(token, yieldBetweenChunks: false))
        {
        }
    }

    private void Wake()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    private void RunLoop()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _wakeSignal.Wait(token);
                while (RunOnePass(token, yieldBetweenChunks: true))
                {
                }

                PassCompleted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Archive proof commitment reclaim failed; the rows stay on disk and the next floor move retries.", e);
            }
        }
    }

    private bool RunOnePass(CancellationToken token, bool yieldBetweenChunks)
    {
        ulong dropped = metadata.DroppedThroughEpoch;
        if (dropped < metadata.RetainedFromEpoch)
        {
            long startedAt = Stopwatch.GetTimestamp();
            CarryForward(_accounts, FlatHistoryColumns.AccountCommitments, dropped, token, yieldBetweenChunks);
            CarryForward(_storages, FlatHistoryColumns.StorageCommitments, dropped, token, yieldBetweenChunks);
            _accounts.RemoveEpoch(dropped, CommitmentKeyLayout.FineTier);
            _accounts.RemoveEpoch(dropped, CommitmentKeyLayout.CoarseTier);
            _storages.RemoveEpoch(dropped, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(dropped, CommitmentKeyLayout.CoarseTier);
            metadata.TryRaiseDroppedThroughEpoch(dropped + 1);
            if (_logger.IsInfo) _logger.Info($"Archive proof commitment epoch {dropped} reclaimed in {Stopwatch.GetElapsedTime(startedAt)}: every node still live was carried into epoch {dropped + 1} first, then the epoch's files were unlinked.");
            return true;
        }

        ulong demoted = Math.Max(metadata.DemotedThroughEpoch, dropped);
        if (demoted < metadata.FineFromEpoch)
        {
            _accounts.RemoveEpoch(demoted, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(demoted, CommitmentKeyLayout.FineTier);
            metadata.TryRaiseDemotedThroughEpoch(demoted + 1);
            return true;
        }

        return false;
    }

    private void CarryForward(CommitmentStore store, FlatHistoryColumns column, ulong epoch, CancellationToken token, bool yieldBetweenChunks)
    {
        CarryForward(store, column, epoch, CommitmentKeyLayout.FineTier, token, yieldBetweenChunks);
        CarryForward(store, column, epoch, CommitmentKeyLayout.CoarseTier, token, yieldBetweenChunks);
    }

    private void CarryForward(CommitmentStore store, FlatHistoryColumns column, ulong epoch, byte tier, CancellationToken token, bool yieldBetweenChunks)
    {
        ulong target = tier == CommitmentKeyLayout.FineTier ? policy.EpochStart(epoch + 1) : policy.WindowAtOrBelow(policy.EpochStart(epoch + 1));
        Span<byte> cursor = stackalloc byte[CommitmentKeyLayout.MaxKeyLength + 1];
        Span<byte> upper = stackalloc byte[CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength];
        int cursorLength = CommitmentKeyLayout.WriteEpochTier(cursor, epoch, tier);
        CommitmentKeyLayout.WriteEpochTier(upper, epoch, (byte)(tier + 1));

        byte[] prefix = ArrayPool<byte>.Shared.Rent(CommitmentKeyLayout.MaxPrefixLength);
        byte[] newest = ArrayPool<byte>.Shared.Rent(ParentRowCodec.MaxBranchRowLength);
        byte[] carried = ArrayPool<byte>.Shared.Rent(ParentRowCodec.MaxBranchRowLength);
        ChildVector vector = ChildVector.Rent();
        IColumnsWriteBatch<FlatHistoryColumns>? batch = null;
        int writesInBatch = 0;
        int nodesInChunk = 0;
        long chunkStartedAt = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int prefixLength;
                int newestLength;
                using (ISortedView view = store.Sorted.GetViewBetween(cursor[..cursorLength], upper, ReadFlags.HintCacheMiss))
                {
                    if (!view.MoveNext()) break;

                    ReadOnlySpan<byte> key = view.CurrentKey;
                    prefixLength = key.Length - KeyOverhead;
                    if (prefixLength <= 0 || prefixLength > CommitmentKeyLayout.MaxPrefixLength)
                    {
                        cursorLength = Advance(cursor, key);
                        continue;
                    }

                    key.Slice(CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength, prefixLength).CopyTo(prefix);
                    ReadOnlySpan<byte> value = view.CurrentValue;
                    newestLength = Math.Min(value.Length, newest.Length);
                    value[..newestLength].CopyTo(newest);
                }

                ReadOnlySpan<byte> node = prefix.AsSpan(0, prefixLength);
                if (!HasRowIn(store, node, epoch + 1))
                {
                    int carriedLength = Compose(store, node, epoch, newest.AsSpan(0, newestLength), vector, carried);
                    if (carriedLength > 0)
                    {
                        lock (metadata.WindowWriteLock)
                        {
                            Span<byte> existing = store.GetExactSpan(node, target);
                            bool present = existing.Length > 0;
                            store.Release(existing);
                            if (!present)
                            {
                                batch ??= history.StartWriteBatch();
                                store.Write(node, target, carried.AsSpan(0, carriedLength), batch.GetColumnBatch(column));
                                if (++writesInBatch >= WritesPerBatch)
                                {
                                    batch.Dispose();
                                    batch = null;
                                    writesInBatch = 0;
                                }
                            }
                        }
                    }
                }

                cursorLength = SkipNode(cursor, epoch, tier, node);
                if (++nodesInChunk < NodesPerChunk) continue;

                nodesInChunk = 0;
                batch?.Dispose();
                batch = null;
                writesInBatch = 0;
                if (yieldBetweenChunks)
                {
                    TimeSpan worked = Stopwatch.GetElapsedTime(chunkStartedAt);
                    if (worked > TimeSpan.Zero) token.WaitHandle.WaitOne(worked);
                    chunkStartedAt = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            batch?.Dispose();
            ChildVector.Return(vector);
            ArrayPool<byte>.Shared.Return(prefix);
            ArrayPool<byte>.Shared.Return(newest);
            ArrayPool<byte>.Shared.Return(carried);
        }
    }

    private static bool HasRowIn(CommitmentStore store, ReadOnlySpan<byte> prefix, ulong epoch)
    {
        using CommitmentStore.RowChain chain = store.OpenNewestInEpoch(prefix, epoch);
        return chain.MoveNext();
    }

    private static int Compose(CommitmentStore store, ReadOnlySpan<byte> prefix, ulong epoch, ReadOnlySpan<byte> newest, ChildVector vector, Span<byte> destination)
    {
        if (ParentRowCodec.IsEmptyRow(newest) || ParentRowCodec.IsWholeNodeRow(newest))
        {
            newest.CopyTo(destination);
            return newest.Length;
        }

        if (!ParentRowCodec.IsBranchRow(newest)) return 0;

        ushort presence = ParentRowCodec.Presence(newest);
        ushort remaining = presence;
        vector.Clear();
        using (CommitmentStore.RowChain chain = store.OpenNewestInEpoch(prefix, epoch))
        {
            while (remaining != 0 && chain.MoveNext())
            {
                ReadOnlySpan<byte> row = chain.CurrentValue;
                if (!ParentRowCodec.IsBranchRow(row)) break;

                remaining &= (ushort)~ParentRowCodec.Fill(row, remaining, vector);
            }
        }

        return remaining == 0 ? ParentRowCodec.EncodeBranch(ParentRowCodec.LastBlock(newest), presence, presence, vector, destination) : 0;
    }

    private static int SkipNode(Span<byte> cursor, ulong epoch, byte tier, ReadOnlySpan<byte> prefix)
    {
        int length = CommitmentKeyLayout.WriteEpochTier(cursor, epoch, tier);
        prefix.CopyTo(cursor[length..]);
        length += prefix.Length;
        cursor.Slice(length, CommitmentKeyLayout.SuffixLength).Fill(0xFF);
        cursor[length + CommitmentKeyLayout.SuffixLength] = 0x00;
        return length + CommitmentKeyLayout.SuffixLength + 1;
    }

    private static int Advance(Span<byte> cursor, ReadOnlySpan<byte> key)
    {
        key.CopyTo(cursor);
        cursor[key.Length] = 0x00;
        return key.Length + 1;
    }

    private static bool TryFloor(ulong headEpoch, int keep, out ulong keepFrom)
    {
        keepFrom = 0;
        if (headEpoch + 1 <= (ulong)keep) return false;

        keepFrom = headEpoch + 1 - (ulong)keep;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        _loop?.Join();
        _cts.Dispose();
        _wakeSignal.Dispose();
    }
}
