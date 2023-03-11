// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Facade.Proxy;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleProxyFactory : ModuleFactoryBase<IEthRpcModule>
    {
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly IWallet _wallet;

        public EthModuleProxyFactory(IEthJsonRpcClientProxy? ethJsonRpcClientProxy, IWallet? wallet)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy ?? throw new ArgumentNullException(nameof(ethJsonRpcClientProxy));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        }

        public override IEthRpcModule Create() => new EthRpcModuleProxy(_ethJsonRpcClientProxy, _wallet);
    }
}
