//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Baseline
{
    public class BaselineModuleFactory : ModuleFactoryBase<IBaselineModule>
    {
        private readonly ISpecProvider _specProvider;
        private readonly ITxPool _txPool;
        private readonly IWallet _wallet;
        private readonly ILogManager _logManager;

        public BaselineModuleFactory(ITxPool txPool,
            IWallet wallet,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public override IBaselineModule Create()
        {
            
            TxPoolBridge txPoolBridge = new TxPoolBridge(_txPool, _wallet, Timestamper.Default, _specProvider.ChainId);
            
            return new BaselineModule(txPoolBridge, _logManager);
        }
    }
}