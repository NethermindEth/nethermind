//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(MigrateConfigs), typeof(SetupKeyStore))]
    public class InitializeBlockTree : IStep
    {
        private readonly IBasicApi _get;
        private readonly IApiWithStores _set;

        public InitializeBlockTree(INethermindApi api)
        {
            (_get, _set) = api.ForInit;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            IInitConfig initConfig = _get.Config<IInitConfig>();
            IBloomConfig bloomConfig = _get.Config<IBloomConfig>();

            IFileStoreFactory fileStoreFactory = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                ? (IFileStoreFactory) new InMemoryDictionaryFileStoreFactory()
                : new FixedSizeFileStoreFactory(Path.Combine(initConfig.BaseDbPath, DbNames.Bloom), DbNames.Bloom, Bloom.ByteLength);

            IBloomStorage? bloomStorage =
                _set.BloomStorage = bloomConfig.Index
                    ? new BloomStorage(bloomConfig, _get.DbProvider!.BloomDb, fileStoreFactory)
                    : (IBloomStorage) NullBloomStorage.Instance;

            _get.DisposeStack.Push(bloomStorage);

            IChainLevelInfoRepository? chainLevelInfoRepository =
                _set.ChainLevelInfoRepository = new ChainLevelInfoRepository(_get.DbProvider!.BlockInfosDb);

            IBlockTree? blockTree = _set.BlockTree = new BlockTree(
                _get.DbProvider,
                chainLevelInfoRepository,
                _get.SpecProvider,
                bloomStorage,
                _get.Config<ISyncConfig>(),
                _get.LogManager);
            
            ISigner signer = NullSigner.Instance;
            ISignerStore signerStore = NullSigner.Instance;
            if (_get.Config<IMiningConfig>().Enabled)
            {
                Signer signerAndStore = new(_get.SpecProvider!.ChainId, _get.OriginalSignerKey, _get.LogManager);
                signer = signerAndStore;
                signerStore = signerAndStore;
            }

            _set.EngineSigner = signer;
            _set.EngineSignerStore = signerStore;

            ReceiptsRecovery receiptsRecovery = new(_get.EthereumEcdsa, _get.SpecProvider);
            IReceiptStorage? receiptStorage = _set.ReceiptStorage
                = initConfig.StoreReceipts ? (IReceiptStorage?) new PersistentReceiptStorage(_get.DbProvider.ReceiptsDb, _get.SpecProvider, receiptsRecovery) : NullReceiptStorage.Instance;
            IReceiptFinder? receiptFinder = _set.ReceiptFinder = new FullInfoReceiptFinder(receiptStorage, receiptsRecovery, blockTree);
            
            LogFinder logFinder = new(
                blockTree,
                receiptFinder,
                bloomStorage,
                _get.LogManager,
                new ReceiptsRecovery(_get.EthereumEcdsa, _get.SpecProvider), 
                1024);

            _set.LogFinder = logFinder;

            return Task.CompletedTask;
        }
    }
}
