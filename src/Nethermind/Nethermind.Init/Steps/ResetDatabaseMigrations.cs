// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.State.Repositories;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(InitializeBlockchain))]
    public class ResetDatabaseMigrations : IStep
    {
        private readonly IApiWithNetwork _api;
        private IReceiptStorage _receiptStorage = null!;
        private IBlockTree _blockTree = null!;
        private IChainLevelInfoRepository _chainLevelInfoRepository = null!;

        public ResetDatabaseMigrations(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            _receiptStorage = _api.ReceiptStorage ?? throw new StepDependencyException(nameof(_api.ReceiptStorage));
            _blockTree = _api.BlockTree ?? throw new StepDependencyException(nameof(_api.BlockTree));
            _chainLevelInfoRepository = _api.ChainLevelInfoRepository ?? throw new StepDependencyException(nameof(_api.ChainLevelInfoRepository));

            IInitConfig initConfig = _api.Config<IInitConfig>();

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
                    ChainLevelInfo? level = _chainLevelInfoRepository.LoadLevel(blockNumber);
                    BlockInfo? firstBlockInfo = level?.BlockInfos.FirstOrDefault();
                    if (firstBlockInfo is not null)
                    {
                        TxReceipt[] receipts = _receiptStorage.Get(firstBlockInfo.BlockHash);
                        if (receipts.Length > 0)
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
