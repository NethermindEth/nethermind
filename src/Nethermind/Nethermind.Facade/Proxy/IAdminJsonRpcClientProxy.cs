// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Facade.Proxy.Models;

namespace Nethermind.Facade.Proxy
{
    public interface IAdminJsonRpcClientProxy
    {
        Task<RpcResult<PeerInfoModel[]>> admin_peers(bool includeDetails);
    }
}
