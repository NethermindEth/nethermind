// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcDuplexClient: IAsyncDisposable
    {
        string Id { get; }
        Task SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken = default);
        event EventHandler Closed;
    }
}
