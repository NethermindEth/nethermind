// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Abi;
using Nethermind.Baseline.Database;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
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
