// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(Block block, GethTraceOptions options, CancellationToken cancellationToken);
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
