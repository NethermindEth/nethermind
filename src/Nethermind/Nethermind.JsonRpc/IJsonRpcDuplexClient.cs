// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcDuplexClient : IDisposable
    {
        string Id { get; }
        Task<int> SendJsonRpcResult(JsonRpcResult result, CancellationToken cancellationToken = default);
        event EventHandler Closed;

        /// <summary>Whether this client reports that it has closed.</summary>
        /// <remarks>Legacy implementations that do not expose closed state return false.</remarks>
        bool IsClosed => false;
    }
}
