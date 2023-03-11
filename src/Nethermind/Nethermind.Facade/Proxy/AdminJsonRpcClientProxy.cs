// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Facade.Proxy.Models;

namespace Nethermind.Facade.Proxy
{
    public class AdminJsonRpcClientProxy : IAdminJsonRpcClientProxy
    {
        private readonly IJsonRpcClientProxy _proxy;

        public AdminJsonRpcClientProxy(IJsonRpcClientProxy proxy)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        }

        public Task<RpcResult<PeerInfoModel[]>> admin_peers(bool includeDetails)
            => _proxy.SendAsync<PeerInfoModel[]>(nameof(admin_peers), includeDetails);
    }
}
