// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(
        typeof(InitializePlugins),
        typeof(InitializeBlockTree),
        typeof(SetupKeyStore),
        typeof(InitializePrecompiles)
    )]
    public class InitializeBlockchain(INethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider, ITxGossipPolicy txGossipPolicy) : IStep
    {
        private readonly INethermindApi _api = api;
        protected readonly ITxGossipPolicy _txGossipPolicy = txGossipPolicy;

        public async Task Execute(CancellationToken _) => await InitBlockchain();

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        protected virtual Task InitBlockchain()
        {
            (IApiWithStores getApi, IApiWithBlockchain setApi) = _api.ForBlockchain;
            setApi.TransactionComparerProvider = new TransactionComparerProvider(getApi.SpecProvider!, getApi.BlockTree!.AsReadOnly());

            IInitConfig initConfig = getApi.Config<IInitConfig>();
            IBlocksConfig blocksConfig = getApi.Config<IBlocksConfig>();

            ThisNodeInfo.AddInfo("Gaslimit     :", $"{blocksConfig.TargetBlockGasLimit:N0}");
            ThisNodeInfo.AddInfo("ExtraData    :", Utf8.IsValid(blocksConfig.GetExtraDataBytes()) ?
                blocksConfig.ExtraData :
                "- binary data -");

            ITxPool txPool = _api.TxPool = CreateTxPool(chainHeadInfoProvider);

            if (blocksConfig.SpeculativeCoverageDiag)
            {
                // Measures the ceiling for speculative pre-execution: of each suggested
                // block's transactions, how many were already in the local pool (and could
                // therefore have been executed before the block arrived). Subscribed before
                // processing so inclusion has not yet evicted them.
                Logging.ILogger coverageLogger = getApi.LogManager.GetClassLogger<InitializeBlockchain>();
                long coverageTotalTxs = 0;
                long coveragePooledTxs = 0;
                long coverageTotalGas = 0;
                long coveragePooledGas = 0;
                getApi.BlockTree!.NewSuggestedBlock += (_, blockEventArgs) =>
                {
                    // Header-only suggests carry no body; never throw inside the suggest path.
                    if (blockEventArgs.Block is not { } suggested) return;
                    Transaction[] transactions = suggested.Transactions;
                    if (transactions.Length == 0) return;
                    int inPool = 0;
                    long blockGas = 0;
                    long inPoolGas = 0;
                    foreach (Transaction transaction in transactions)
                    {
                        // Gas limit, not gas used (unknown pre-execution) — overstates absolute
                        // cost but the pooled/total ratio is what the gate reads.
                        blockGas += transaction.GasLimit;
                        if (transaction.Hash is not null && txPool.ContainsTx(transaction.Hash, transaction.Type))
                        {
                            inPool++;
                            inPoolGas += transaction.GasLimit;
                        }
                    }
                    long total = Interlocked.Add(ref coverageTotalTxs, transactions.Length);
                    long pooled = Interlocked.Add(ref coveragePooledTxs, inPool);
                    long totalGas = Interlocked.Add(ref coverageTotalGas, blockGas);
                    long pooledGas = Interlocked.Add(ref coveragePooledGas, inPoolGas);
                    if (coverageLogger.IsInfo)
                        coverageLogger.Info($"SpecExecDiag: block {suggested.Number} txs {transactions.Length}, inPool {inPool} ({100.0 * inPool / transactions.Length:F1}%), gas {100.0 * inPoolGas / blockGas:F1}%; cumulative txs {100.0 * pooled / total:F1}%, gas {100.0 * pooledGas / totalGas:F1}%");
                };
            }

            _api.BlockPreprocessor.AddFirst(
                new RecoverSignatures(getApi.EthereumEcdsa, getApi.SpecProvider, getApi.LogManager));

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(getApi.Wallet, getApi.SpecProvider!.ChainId);
            TxSealer nonceReservingTxSealer =
                new(txSigner, getApi.Timestamper);
            INonceManager nonceManager = new NonceManager(chainHeadInfoProvider.ReadOnlyStateProvider);
            setApi.NonceManager = nonceManager;
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, nonceManager, getApi.EthereumEcdsa!);
            setApi.BlockProductionPolicy = CreateBlockProductionPolicy();

            IBranchProcessor mainBranchProcessor = setApi.MainProcessingContext.BranchProcessor;

            BackgroundTaskScheduler backgroundTaskScheduler = new(
                mainBranchProcessor,
                chainHeadInfoProvider,
                initConfig.BackgroundTaskConcurrency,
                initConfig.BackgroundTaskMaxNumber,
                _api.LogManager);
            setApi.BackgroundTaskScheduler = backgroundTaskScheduler;
            _api.DisposeStack.Push(backgroundTaskScheduler);

            ICensorshipDetectorConfig censorshipDetectorConfig = _api.Config<ICensorshipDetectorConfig>();
            if (censorshipDetectorConfig.Enabled)
            {
                CensorshipDetector censorshipDetector = new(
                    _api.BlockTree!,
                    txPool,
                    CreateTxPoolTxComparer(),
                    mainBranchProcessor,
                    _api.LogManager,
                    censorshipDetectorConfig
                );
                setApi.CensorshipDetector = censorshipDetector;
                _api.DisposeStack.Push(censorshipDetector);
            }

            return Task.CompletedTask;
        }

        protected virtual IBlockProductionPolicy CreateBlockProductionPolicy() =>
            new BlockProductionPolicy(_api.Config<IMiningConfig>());

        protected virtual ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider)
        {
            TxPool.TxPool txPool = new(_api.EthereumEcdsa!,
                _api.BlobTxStorage ?? NullBlobTxStorage.Instance,
                chainHeadInfoProvider,
                _api.Config<ITxPoolConfig>(),
                _api.TxValidator!,
                _api.LogManager,
                CreateTxPoolTxComparer(),
                _txGossipPolicy,
                null,
                _api.HeadTxValidator
            );

            _api.DisposeStack.Push(txPool);
            return txPool;
        }

        protected IComparer<Transaction> CreateTxPoolTxComparer() => _api.TransactionComparerProvider!.GetDefaultComparer();
    }
}
