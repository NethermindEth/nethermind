// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// Checks are kept in insertion order up to <see cref="Capacity"/>, the oldest evicted first, which the engine API
/// permits: the consensus layer resubmits the original inputs after <c>Unknown inclusion check</c>. A check that
/// cannot be decided, its state or execution gas counters being unavailable, is forgotten rather than completed,
/// so it is never reported as satisfied or excused.
/// </remarks>
public sealed class InclusionChecks(IInclusionListComplianceEvaluator evaluator, ILogManager logManager) : IInclusionChecks, IDisposable
{
    public const int Capacity = 256;

    private readonly ILogger _logger = logManager.GetClassLogger<InclusionChecks>();
    private readonly Lock _lock = new();
    private readonly Dictionary<Hash256AsKey, LinkedListNode<Check>> _checks = [];
    private readonly LinkedList<Check> _order = new();
    private readonly CancellationTokenSource _disposed = new();

    private sealed class Check(Hash256 id)
    {
        public Hash256 Id { get; } = id;
        public Task<bool?>? Verdict { get; set; }

        public bool? Current => Verdict is { IsCompletedSuccessfully: true } completed ? completed.Result : null;
    }

    public bool? Start(Hash256 inclusionCheckId, ExecutionPayload executionPayload, byte[][][] inclusionLists, OmissionClaim[] claims)
    {
        Check check;
        lock (_lock)
        {
            if (_checks.TryGetValue(inclusionCheckId, out LinkedListNode<Check>? known)) return known.Value.Current;

            check = new Check(inclusionCheckId);
            _checks[inclusionCheckId] = _order.AddLast(check);
            while (_checks.Count > Capacity) Remove(_order.First!.Value);
        }

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

    private async Task<bool?> RunAsync(Check check, ExecutionPayload executionPayload, byte[][][] inclusionLists, OmissionClaim[] claims)
    {
        bool? verdict = null;
        try
        {
            Result<Block> block = executionPayload.TryGetBlock();
            if (block.IsError)
            {
                if (_logger.IsWarn) _logger.Warn($"Inclusion check {check.Id} cannot rebuild block {executionPayload.BlockHash}: {block.Error}");
            }
            else
            {
                Transaction[][] decoded = InclusionListDecoder.DecodeLists(inclusionLists);
                verdict = await evaluator.EvaluateAsync(block.Data, decoded, claims, _disposed.Token);
                if (verdict is null && _logger.IsDebug) _logger.Debug($"Inclusion check {check.Id} for block {executionPayload.BlockHash} is undecidable without its state or execution gas counters; forgetting it.");
            }
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Inclusion check {check.Id} for block {executionPayload.BlockHash} failed.", e);
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
