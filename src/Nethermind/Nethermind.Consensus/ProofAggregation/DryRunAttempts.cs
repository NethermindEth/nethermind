// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Counts, per head, the admission checks a transaction failed before proof-capacity eviction.</summary>
/// <remarks>Kept outside the pool's known set: the check runs out of the context a submission sees (witnesses, predecessors and
/// evicted reservations aside), so its rejection must not refuse the transaction elsewhere. A rejection the transaction's content
/// decided uses up its checks at once; any other, a deferral included, uses one, so a rejection the pool's contents decided can
/// clear within the head while a replay simulates at most <see cref="MaxPerKey"/> times per key and <see cref="MaxPerTransaction"/>
/// in all. Not thread-safe; the caller serialises access.</remarks>
internal sealed class DryRunAttempts
{
    /// <summary>Failed checks per head for one key.</summary>
    internal const int MaxPerKey = 2;

    /// <summary>Failed checks per head for one transaction across all sources, so sources cannot multiply them.</summary>
    internal const int MaxPerTransaction = 4;

    /// <summary>Keys tracked per head; beyond it a new transaction's rejection is not counted.</summary>
    private const int MaxKeys = 4_096;

    /// <summary>Rejections that depend only on what a transaction's hash covers and on the head, never on the pool's contents,
    /// reservations or budgets, so repeating its check on the head would reach them again.</summary>
    private static readonly HashSet<AcceptTxResult> ContentBoundRejections =
    [
        AcceptTxResult.FailedToResolveSender, AcceptTxResult.Int256Overflow, AcceptTxResult.Invalid, AcceptTxResult.NotSupportedTxType,
        AcceptTxResult.SignFailed, AcceptTxResult.OldNonce, AcceptTxResult.SenderIsContract, AcceptTxResult.FrameTxExpired,
        AcceptTxResult.FrameTxNoPayer, AcceptTxResult.FrameTxUnrecognizedPrefix, AcceptTxResult.FrameTxVerifyAfterPrefix,
        AcceptTxResult.FrameTxMisplacedExpiryFrame, AcceptTxResult.FrameTxMisplacedRecentRootFrame, AcceptTxResult.FrameSimulationFailed,
    ];

    private Hash256? _head;
    private readonly Dictionary<(object?, ValueHash256, TxHandlingOptions), int> _byKey = [];
    private readonly Dictionary<ValueHash256, int> _byTransaction = [];

    /// <summary>Whether <paramref name="transaction"/> has used up its checks on <paramref name="head"/>.</summary>
    public bool IsExhausted(Hash256? head, Transaction transaction, object? source, TxHandlingOptions handling)
    {
        Roll(head);
        return _byKey.GetValueOrDefault(Key(transaction, source, handling)) >= MaxPerKey
            || _byTransaction.GetValueOrDefault(transaction.Hash!.ValueHash256) >= MaxPerTransaction;
    }

    /// <summary>Records that <paramref name="transaction"/> failed its check on <paramref name="head"/> with <paramref name="result"/>.</summary>
    public void Record(Hash256? head, Transaction transaction, object? source, TxHandlingOptions handling, AcceptTxResult result)
    {
        Roll(head);
        (object?, ValueHash256, TxHandlingOptions) key = Key(transaction, source, handling);
        if (_byKey.Count >= MaxKeys && !_byKey.ContainsKey(key)) return;
        // A sidecar is not covered by the hash and may be a source's corruption, so a blob-carrying rejection only counts.
        _byKey[key] = ContentBoundRejections.Contains(result) && !transaction.CarriesBlobs ? MaxPerKey : _byKey.GetValueOrDefault(key) + 1;
        _byTransaction[transaction.Hash!.ValueHash256] = _byTransaction.GetValueOrDefault(transaction.Hash.ValueHash256) + 1;
    }

    /// <summary>By hash and handling, and for a blob-carrying transaction by source too (local ones as <see langword="null"/>),
    /// since its hash does not cover the sidecar and one source's corrupt copy must not use up another's.</summary>
    private static (object?, ValueHash256, TxHandlingOptions) Key(Transaction transaction, object? source, TxHandlingOptions handling) =>
        (transaction.CarriesBlobs ? source : null, transaction.Hash!.ValueHash256, handling);

    private void Roll(Hash256? head)
    {
        if (_head == head) return;
        _head = head;
        _byKey.Clear();
        _byTransaction.Clear();
    }
}
