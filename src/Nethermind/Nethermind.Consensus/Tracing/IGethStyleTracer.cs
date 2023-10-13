// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing;

public interface IGethStyleTracer
{
    GethLikeTxTrace Trace(Commitment txHash, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(long blockNumber, Transaction transaction, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(long blockNumber, int txIndex, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(Commitment blockHash, int txIndex, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace Trace(Rlp blockRlp, Commitment txHash, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace? Trace(BlockParameter blockParameter, Transaction tx, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace[] TraceBlock(BlockParameter blockParameter, GethTraceOptions options, CancellationToken cancellationToken);
    GethLikeTxTrace[] TraceBlock(Rlp blockRlp, GethTraceOptions options, CancellationToken cancellationToken);
    IEnumerable<string> TraceBlockToFile(Commitment blockHash, GethTraceOptions options, CancellationToken cancellationToken);
}
