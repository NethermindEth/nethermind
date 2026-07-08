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
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Diagnostics;
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

            IBlocksConfig blocksConfig = getApi.Config<IBlocksConfig>();

            if (blocksConfig.ReadTraceOutput is not null) ReadTrace.Configure(blocksConfig.ReadTraceOutput, blocksConfig.ReadTraceBlocks);

            ThisNodeInfo.AddInfo("Gaslimit     :", $"{blocksConfig.TargetBlockGasLimit:N0}");
            ThisNodeInfo.AddInfo("ExtraData    :", Utf8.IsValid(blocksConfig.GetExtraDataBytes()) ?
                blocksConfig.ExtraData :
                "- binary data -");

            ITxPool txPool = _api.TxPool = CreateTxPool(chainHeadInfoProvider);

            // TODO: can take the tx sender from plugin here maybe
            ITxSigner txSigner = new WalletTxSigner(getApi.Wallet, getApi.SpecProvider!.ChainId);
            TxSealer nonceReservingTxSealer =
                new(txSigner, getApi.Timestamper);
            setApi.TxSender = new TxPoolSender(txPool, nonceReservingTxSealer, _api.NonceManager!, getApi.EthereumEcdsa!);

            IBranchProcessor mainBranchProcessor = setApi.MainProcessingContext.BranchProcessor;

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
