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

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain))]
    public class ResetDatabaseMigrations : IStep
    {
        private readonly IApiWithNetwork _api;
        [NotNull]
        private IReceiptStorage? _receiptStorage;
        [NotNull]
        private IBlockTree? _blockTree;
        [NotNull]
        private IChainLevelInfoRepository? _chainLevelInfoRepository;

        public ResetDatabaseMigrations(INethermindApi api)
        {
            _api = api;
        }
        
        public Task Execute(CancellationToken cancellationToken)
        {
            _receiptStorage = _api.ReceiptStorage ?? throw new StepDependencyException(nameof(_api.ReceiptStorage));
            _blockTree = _api.BlockTree ?? throw new StepDependencyException(nameof(_api.BlockTree));
            _chainLevelInfoRepository = _api.ChainLevelInfoRepository ?? throw new StepDependencyException(nameof(_api.ChainLevelInfoRepository));
            
            var initConfig = _api.Config<IInitConfig>();

            if (initConfig.StoreReceipts)
            {
                ResetMigrationIndexIfNeeded();
            }

            return Task.CompletedTask;
        }
        
        private void ResetMigrationIndexIfNeeded()
        {
            ReceiptsRecovery recovery = new ReceiptsRecovery(_api.EthereumEcdsa, _api.SpecProvider);
            
            if (_receiptStorage.MigratedBlockNumber != long.MaxValue)
            {
                long blockNumber = _blockTree.Head?.Number ?? 0;
                while (blockNumber > 0)
                {
                    var level = _chainLevelInfoRepository.LoadLevel(blockNumber);
                    var firstBlockInfo = level?.BlockInfos.FirstOrDefault();
                    if (firstBlockInfo != null)
                    {
                        var receipts = _receiptStorage.Get(firstBlockInfo.BlockHash);
                        if (receipts?.Length > 0)
                        {
                            if (recovery.NeedRecover(receipts))
                            {
                                _receiptStorage.MigratedBlockNumber = long.MaxValue;
                            }

                            break;
                        }
                    }

                    blockNumber--;
                }
            }
        }
    }
}
