// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing;

public interface IGethStyleTracer
{
    /// <summary>
    /// Trace a single transaction. When <paramref name="writer"/> is non-null and the selected
    /// tracer supports streaming, struct-log entries are serialised into the writer as they are
    /// produced (returned value contains only the header fields).
    /// </summary>
    GethLikeTxTrace? Trace(Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    GethLikeTxTrace? Trace(ulong blockNumber, Transaction transaction, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace? Trace(ulong blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    GethLikeTxTrace? Trace(Hash256 blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    GethLikeTxTrace? Trace(Rlp blockRlp, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    GethLikeTxTrace? Trace(Block block, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    GethLikeTxTrace? Trace(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);

    /// <summary>
    /// Trace all transactions in a block. When <paramref name="writer"/> is non-null, each per-tx
    /// envelope is written directly into the writer as the block executes and the return value is
    /// an empty collection (nothing is buffered).
    /// </summary>
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Block block, GethTraceOptions options, CancellationToken cancellationToken, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null);
    /// <summary>
    /// Replays <paramref name="blockHash"/> and returns the world-state root after each transaction
    /// in execution order. EIP-4788/EIP-2935 system calls and withdrawals do not produce an entry.
    /// </summary>
    /// <param name="blockHash">Hash of a canonical or bad-stored block to replay.</param>
    /// <param name="options">Trace options (state overrides are applied to the parent base state).</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Post-tx state roots in execution order; failed transactions still produce a root.</returns>
    IReadOnlyCollection<Hash256> TraceBlockIntermediateRoots(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken);
    IEnumerable<string> TraceBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken);
    IEnumerable<string> TraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken);
}
