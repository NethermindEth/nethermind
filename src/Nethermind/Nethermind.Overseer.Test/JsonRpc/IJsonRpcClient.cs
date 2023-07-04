// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Overseer.Test.JsonRpc
{
    public interface IJsonRpcClient
    {
        Task<JsonRpcResponse<T>> PostAsync<T>(string method);
        Task<JsonRpcResponse<T>> PostAsync<T>(string method, object[] @params);
    }
}
