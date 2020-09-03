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
using System.IO.Abstractions;
using Nethermind.Abi;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Facade.Transactions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Baseline.JsonRpc
{
    public class BaselineModuleFactory : ModuleFactoryBase<IBaselineModule>
    {
        private readonly ISpecProvider _specProvider;
        private readonly IFileSystem _fileSystem;
        private readonly ITxPool _txPool;
        private readonly ILogFinder _logFinder;
        private readonly IBlockFinder _blockFinder;
        private readonly IStateReader _stateReader;
        private readonly IWallet _wallet;
        private readonly ILogManager _logManager;
        private readonly IAbiEncoder _abiEncoder;
        
        public BaselineModuleFactory(ITxPool txPool,
            IStateReader stateReader,
            ILogFinder logFinder,
            IBlockFinder blockFinder,
            IAbiEncoder abiEncoder,
            IWallet wallet,
            ISpecProvider specProvider,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public override IBaselineModule Create()
        {
            WalletTxSigner txSigner = new WalletTxSigner(_wallet, _specProvider.ChainId);
            TxPoolBridge txPoolBridge = new TxPoolBridge(_txPool, txSigner, Timestamper.Default);
            return new BaselineModule(
                txPoolBridge,
                _stateReader,
                _logFinder,
                _blockFinder,
                _abiEncoder,
                _fileSystem,
                new MemDb(),
                _logManager);
        }
    }
}
