// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

internal interface IJsonRpcResponseSink
{
    long BytesWritten { get; }
    bool StopRequested { get; }

    ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken);
    ValueTask BeginBatchAsync(CancellationToken cancellationToken);
    ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken);
    ValueTask EndBatchAsync(CancellationToken cancellationToken);
}
