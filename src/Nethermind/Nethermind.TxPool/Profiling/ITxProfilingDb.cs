// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool.Profiling;

/// <summary>
/// Records transaction gossip and tx-pool lifecycle events for local diagnostics.
/// </summary>
public interface ITxProfilingDb
{
    /// <summary>
    /// Gets the current JSONL file path, or <c>null</c> when profiling is disabled.
    /// </summary>
    string? FilePath { get; }

    /// <summary>
    /// Gets the number of records dropped because the writer queue was full.
    /// </summary>
    long DroppedRecords { get; }

    /// <summary>
    /// Records an event about a transaction hash when a full transaction is not available.
    /// </summary>
    void RecordHash(
        string eventName,
        Hash256? txHash,
        string? peer = null,
        string? protocol = null,
        string? direction = null,
        string? reason = null,
        TxType? txType = null,
        int? txSize = null);

    /// <summary>
    /// Records an event about a full transaction.
    /// </summary>
    void RecordTx(
        string eventName,
        Transaction? tx,
        string? peer = null,
        string? protocol = null,
        string? direction = null,
        string? reason = null,
        AcceptTxResult? result = null);

    /// <summary>
    /// Records an event about a retry-cache resource id.
    /// </summary>
    void RecordResource(
        string eventName,
        string resourceId,
        string? peer = null,
        string? reason = null);
}

/// <summary>
/// Event names emitted by <see cref="ITxProfilingDb"/>.
/// </summary>
public static class TxProfilingEvents
{
    /// <summary>Remote peer announced a transaction hash.</summary>
    public const string TxHashAnnounced = "tx_hash_announced";

    /// <summary>Local node announced a transaction hash to a peer.</summary>
    public const string TxHashAnnouncedToPeer = "tx_hash_announced_to_peer";

    /// <summary>Remote hash announcement was ignored because the transaction was already known.</summary>
    public const string TxHashIgnoredKnown = "tx_hash_ignored_known";

    /// <summary>Remote peer sent a full transaction.</summary>
    public const string TxReceivedFromPeer = "tx_received_from_peer";

    /// <summary>Local node requested a transaction from a peer.</summary>
    public const string TxRequested = "tx_requested";

    /// <summary>Local node postponed requesting a transaction while waiting for another peer or local state.</summary>
    public const string TxRequestPostponed = "tx_request_postponed";

    /// <summary>Retry cache requested a transaction from another announcing peer.</summary>
    public const string TxRequestRetried = "tx_request_retried";

    /// <summary>Local node served a peer's transaction request.</summary>
    public const string TxRequestServed = "tx_request_served";

    /// <summary>Local node skipped serving or requesting a transaction.</summary>
    public const string TxRequestSkipped = "tx_request_skipped";

    /// <summary>Local node sent a full transaction to a peer.</summary>
    public const string TxSentToPeer = "tx_sent_to_peer";

    /// <summary>Peer-scoped transaction submission result.</summary>
    public const string TxSubmitResult = "tx_submit_result";

    /// <summary>Tx-pool transaction submission result.</summary>
    public const string TxPoolSubmitResult = "tx_pool_submit_result";

    /// <summary>Transaction was evicted from the tx pool.</summary>
    public const string TxPoolEvicted = "tx_pool_evicted";

    /// <summary>Transaction was removed from the tx pool.</summary>
    public const string TxPoolRemoved = "tx_pool_removed";
}
