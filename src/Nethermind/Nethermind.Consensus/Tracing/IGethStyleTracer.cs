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
    GethLikeTxTrace Trace(Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(long blockNumber, Transaction transaction, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(long blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(Hash256 blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(Rlp blockRlp, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(Block block, Hash256 txHash, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace? Trace(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Streaming variant of <see cref="Trace(Hash256, GethTraceOptions, CancellationToken)"/>.
    /// Struct-log entries are serialised into <paramref name="writer"/> as the EVM produces them,
    /// then discarded; only the header fields (gas / failed / returnValue) are returned.
    /// </summary>
    GethLikeTxTrace? TraceStreaming(Hash256 txHash, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);

    /// <summary>
    /// Streaming variant of <see cref="Trace(BlockParameter, Transaction, GethTraceOptions, CancellationToken)"/>.
    /// See <see cref="TraceStreaming(Hash256, GethTraceOptions, Utf8JsonWriter, PipeWriter, CancellationToken)"/>.
    /// </summary>
    GethLikeTxTrace? TraceStreaming(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);

    GethLikeTxTrace? TraceStreaming(long blockNumber, int txIndex, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
    GethLikeTxTrace? TraceStreaming(Hash256 blockHash, int txIndex, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
    GethLikeTxTrace? TraceStreaming(Rlp blockRlp, Hash256 txHash, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
    GethLikeTxTrace? TraceStreaming(Block block, Hash256 txHash, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);

    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Block block, GethTraceOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Streaming variant of <see cref="TraceBlock(BlockParameter, GethTraceOptions, CancellationToken)"/>.
    /// Each per-tx envelope and its struct-log entries are written directly into the supplied
    /// <see cref="Utf8JsonWriter"/> as the block executes; nothing is buffered.
    /// </summary>
    void TraceBlockStreaming(BlockParameter blockParameter, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
    void TraceBlockStreaming(Rlp blockRlp, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
    void TraceBlockStreaming(Block block, GethTraceOptions options, Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken);
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
