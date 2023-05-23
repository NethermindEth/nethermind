// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcDuplexClient : IDisposable
    {
        string Id { get; }
        Task<int> SendJsonRpcResult(JsonRpcResult result);
        event EventHandler Closed;
    }
}
