// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Client
{
    public interface IJsonRpcClient
    {
        Task<string?> Post(string method, params object?[] parameters);

        Task<T?> Post<T>(string method, params object?[] parameters);
    }
}
