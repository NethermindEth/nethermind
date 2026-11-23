// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Modules.Trace;

public interface ICancellableBlockTracer
{
    ResultWrapper<IEnumerable<ParityTxTraceFromStore>> TraceBlockWithCancellation(BlockParameter blockParameter, string? fork, CancellationToken cancellationToken);
}
