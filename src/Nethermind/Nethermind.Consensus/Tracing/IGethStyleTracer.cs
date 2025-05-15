// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.JsonRpc.Modules;
namespace Nethermind.Consensus.Tracing;

public interface IGethStyleTracer
{
    IEnumerable<string> TraceBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken);
    IEnumerable<string> TraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace? Trace(GethStyleTracerRequest request, CancellationToken cancellationToken);
    IReadOnlyCollection<GethLikeTxTrace> TraceBlock(GethStyleTracerRequest request, CancellationToken cancellationToken);
    
    GethStyleTracerRequest ResolveTraceRequest(Hash256 txHash, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceRequest(long blockNumber, Transaction transaction, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceRequest(long blockNumber, int txIndex, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceRequest(Hash256 blockHash, int txIndex, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceRequest(Rlp blockRlp, Hash256 txHash, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceRequest(Block block, Hash256 txHash, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceRequest(BlockParameter blockParameter, Transaction tx, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceBlockRequest(BlockParameter blockParameter, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceBlockRequest(Rlp blockRlp, GethTraceOptions options);
    GethStyleTracerRequest ResolveTraceBlockRequest(Block block, GethTraceOptions options);
}
