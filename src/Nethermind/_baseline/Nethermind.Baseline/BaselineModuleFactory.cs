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
using Nethermind.Baseline.Database;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Baseline
{
    public class BaselineModuleFactory : ModuleFactoryBase<IBaselineModule>
    {
        private readonly IFileSystem _fileSystem;
        private readonly ITxSender _txSender;
        private readonly ILogFinder _logFinder;
        private readonly IBlockFinder _blockFinder;
        private readonly IStateReader _stateReader;
        private readonly ILogManager _logManager;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IBlockProcessor _blockProcessor;
        private readonly DisposableStack _disposableStack;
        private readonly IDbProvider _dbProvider;
        
        public BaselineModuleFactory(
            ITxSender txSender,
            IStateReader stateReader,
            ILogFinder logFinder,
            IBlockFinder blockFinder,
            IAbiEncoder abiEncoder,
            IFileSystem fileSystem,
            ILogManager logManager,
            IBlockProcessor blockProcessor,
            DisposableStack disposableStack,
            IDbProvider dbProvider)
        {
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _disposableStack = disposableStack ?? throw new ArgumentNullException(nameof(disposableStack));
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
        }
        
        public override IBaselineModule Create()
        {
            return new BaselineModule(
                _txSender,
                _stateReader,
                _logFinder,
                _blockFinder,
                _abiEncoder,
                _fileSystem,
                _dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTree),
                _dbProvider.GetDb<IDb>(BaselineDbNames.BaselineTreeMetadata),
                _logManager,
                _blockProcessor,
                _disposableStack);
        }
    }
}
