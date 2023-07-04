// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Facade.Proxy
{
    public interface IJsonRpcClientProxy
    {
        Task<RpcResult<T>> SendAsync<T>(string method, params object[] @params);
        Task<RpcResult<T>> SendAsync<T>(string method, long id, params object[] @params);
        void SetUrls(params string[] urls);
    }
}
