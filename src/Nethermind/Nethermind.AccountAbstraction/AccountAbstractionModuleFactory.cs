// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionModuleFactory : ModuleFactoryBase<IAccountAbstractionRpcModule>
    {
        private readonly IDictionary<Address, IUserOperationPool> _userOperationPool;
        private readonly Address[] _supportedEntryPoints;

        public AccountAbstractionModuleFactory(IDictionary<Address, IUserOperationPool> userOperationPool, Address[] supportedEntryPoints)
        {
            _userOperationPool = userOperationPool;
            _supportedEntryPoints = supportedEntryPoints;
        }

        public override IAccountAbstractionRpcModule Create()
        {
            return new AccountAbstractionRpcModule(_userOperationPool, _supportedEntryPoints);
        }
    }
}
