// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.EvmPlayground
{
    internal interface IJsonRpcClient
    {
        Task<string> Post(string method, params object[] parameters);
    }
}
