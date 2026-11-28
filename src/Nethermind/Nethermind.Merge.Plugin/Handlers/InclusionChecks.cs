// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>The deferred EIP-7805 inclusion checks this node runs, by identifier (bogota.md § Deferred inclusion checking).</summary>
public interface IInclusionChecks
{
    /// <summary>Schedules the check <paramref name="inclusionCheckId"/> names unless it is already known, and returns
    /// its current verdict without waiting for it.</summary>
    /// <param name="executionPayload">A payload whose execution is <c>VALID</c>, with every field newPayload set.</param>
    /// <returns>The completed verdict, or <c>null</c> while the check is pending.</returns>
    bool? Start(Hash256 inclusionCheckId, ExecutionPayload executionPayload, byte[][][] inclusionLists, OmissionClaim[] claims);

    /// <summary>The current verdict of a known check, never starting or restarting one.</summary>
    /// <param name="verdict">The completed verdict, or <c>null</c> while the check is pending.</param>
    /// <returns><c>false</c> when the identifier is unknown or evicted.</returns>
    bool TryGetVerdict(Hash256 inclusionCheckId, out bool? verdict);
}

/// <inheritdoc cref="IInclusionChecks"/>
/// <remarks>
/// A check runs once per identifier however often its inputs are resent, and its verdict never changes once complete.
/// At most <see cref="Capacity"/> checks are kept. Past it one is evicted, which the engine API permits since the
/// consensus layer resubmits the original inputs after <c>Unknown inclusion check</c>: the oldest whose block is at or
/// below the finalized one, so finalized or pruned from fork choice, else the oldest pending one, else the oldest.
/// A check that cannot be decided, its state or execution gas counters being unavailable, is evicted rather than
/// completed, so it is never reported as satisfied or excused, and a resubmission runs it again. At most
/// <see cref="MaxConcurrentChecks"/> run at once, and an evicted pending check is cancelled.
/// </remarks>
public sealed class InclusionChecks(IInclusionListComplianceEvaluator evaluator, IBlockFinder blockFinder, ILogManager logManager) : IInclusionChecks, IDisposable
{
    public const int Capacity = 256;
    public const int MaxConcurrentChecks = 4;

    private readonly ILogger _logger = logManager.GetClassLogger<InclusionChecks>();
    private readonly Lock _lock = new();
    private readonly Dictionary<Hash256AsKey, LinkedListNode<Check>> _checks = [];
    private readonly LinkedList<Check> _order = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly SemaphoreSlim _running = new(MaxConcurrentChecks);

    private sealed class Check(Hash256 id, ulong blockNumber)
    {
        public Hash256 Id { get; } = id;
        public ulong BlockNumber { get; } = blockNumber;
        public CancellationTokenSource Evicted { get; } = new();
        public Task<bool?>? Verdict { get; set; }

        public bool? Current => Verdict is { IsCompletedSuccessfully: true } completed ? completed.Result : null;
    }

    public bool? Start(Hash256 inclusionCheckId, ExecutionPayload executionPayload, byte[][][] inclusionLists, OmissionClaim[] claims)
    {
        // Read outside the lock, and only near capacity, as it may touch the block tree's storage.
        ulong? finalizedNumber = _checks.Count >= Capacity ? FinalizedNumber() : null;
        Check check;
        Check? evicted = null;
        lock (_lock)
        {
            if (_checks.TryGetValue(inclusionCheckId, out LinkedListNode<Check>? known)) return known.Value.Current;

            check = new Check(inclusionCheckId, executionPayload.BlockNumber);
            _checks[inclusionCheckId] = _order.AddLast(check);
            if (_checks.Count > Capacity) Remove(evicted = EvictionCandidate(finalizedNumber, check));
        }

        evicted?.Evicted.Cancel();
        check.Verdict = Task.Run(() => RunAsync(check, executionPayload, inclusionLists, claims));
        return check.Current;
    }

    public bool TryGetVerdict(Hash256 inclusionCheckId, out bool? verdict)
    {
        lock (_lock)
        {
            bool known = _checks.TryGetValue(inclusionCheckId, out LinkedListNode<Check>? check);
            verdict = known ? check!.Value.Current : null;
            return known;
        }
    }

    private ulong? FinalizedNumber() =>
        blockFinder.FinalizedHash is { } finalized ? blockFinder.FindHeader(finalized, BlockTreeLookupOptions.TotalDifficultyNotNeeded)?.Number : null;

    /// <remarks>bogota.md § Deferred inclusion checking: a completed verdict is retained until a finalized block's
    /// number reaches its block's, unless resources run short, which a full store of other completed verdicts is.
    /// <paramref name="added"/> is the check being made room for, never the one evicted.</remarks>
    private Check EvictionCandidate(ulong? finalizedNumber, Check added)
    {
        Check? oldestPending = null;
        foreach (Check check in _order)
        {
            if (ReferenceEquals(check, added)) continue;
            if (check.BlockNumber <= finalizedNumber) return check;
            if (check.Current is null) oldestPending ??= check;
        }
        return oldestPending ?? _order.First!.Value;
    }

    private async Task<bool?> RunAsync(Check check, ExecutionPayload executionPayload, byte[][][] inclusionLists, OmissionClaim[] claims)
    {
        bool? verdict = null;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token, check.Evicted.Token);
        bool entered = false;
        try
        {
            await _running.WaitAsync(cancellation.Token);
            entered = true;
            Result<Block> block = executionPayload.TryGetBlock();
            if (block.IsError)
            {
                if (_logger.IsWarn) _logger.Warn($"Inclusion check {check.Id} cannot rebuild block {executionPayload.BlockHash}: {block.Error}");
            }
            else
            {
                Transaction[][] decoded = InclusionListDecoder.DecodeLists(inclusionLists);
                verdict = await evaluator.EvaluateAsync(block.Data, decoded, claims, cancellation.Token);
                if (verdict is null && _logger.IsDebug) _logger.Debug($"Inclusion check {check.Id} for block {executionPayload.BlockHash} is undecidable without its state or execution gas counters; forgetting it.");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Inclusion check {check.Id} for block {executionPayload.BlockHash} failed.", e);
        }
        finally
        {
            if (entered) _running.Release();
        }

        if (verdict is null)
        {
            lock (_lock) Remove(check);
        }
        else if (_logger.IsDebug)
        {
            _logger.Debug($"Inclusion check {check.Id} for block {executionPayload.BlockHash}: inclusionListSatisfied={verdict}.");
        }

        return verdict;
    }

    /// <summary>Forgets <paramref name="check"/>, unless a newer check has taken its identifier.</summary>
    private void Remove(Check check)
    {
        if (!_checks.TryGetValue(check.Id, out LinkedListNode<Check>? node) || !ReferenceEquals(node.Value, check)) return;
        _checks.Remove(check.Id);
        _order.Remove(node);
    }

    // Cancelled but never disposed: a check scheduled concurrently with disposal still reads its token.
    public void Dispose() => _disposed.Cancel();
}
