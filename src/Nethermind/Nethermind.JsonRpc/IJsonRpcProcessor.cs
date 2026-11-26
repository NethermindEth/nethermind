// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public interface IJsonRpcProcessor
{
    ValueTask ProcessAsync(
        ReadOnlyMemory<byte> request,
        JsonRpcContext context,
        IJsonRpcResponseSink sink,
        JsonRpcProcessingOptions options,
        CancellationToken cancellationToken = default);

    CancellationToken ProcessExit { get; }
}
