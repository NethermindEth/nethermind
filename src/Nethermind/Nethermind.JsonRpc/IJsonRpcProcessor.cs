// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcProcessor
{
    ValueTask ProcessAsync(
        PipeReader reader,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default);

    CancellationToken ProcessExit { get; }
}
