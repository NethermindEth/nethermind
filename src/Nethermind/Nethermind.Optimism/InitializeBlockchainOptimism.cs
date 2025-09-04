// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Init.Steps;
using Nethermind.Optimism.Rpc;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism(OptimismNethermindApi api, IChainHeadInfoProvider chainHeadInfoProvider) : InitializeBlockchain(api, chainHeadInfoProvider)
{
    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        var withdrawalProcessor = new OptimismWithdrawalProcessor(api.WorldStateManager!.GlobalWorldState, api.LogManager, api.SpecHelper);
        var genesisPostProcessor = new OptimismGenesisPostProcessor(withdrawalProcessor, api.SpecProvider);
        api.GenesisPostProcessor = genesisPostProcessor;

        api.RegisterTxType<DepositTransactionForRpc>(new OptimismTxDecoder<Transaction>(), Always.Valid);
        api.RegisterTxType<LegacyTransactionForRpc>(new OptimismLegacyTxDecoder(), new OptimismLegacyTxValidator(api.SpecProvider!.ChainId));
    }

    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;

    protected override ITxPool CreateTxPool(IChainHeadInfoProvider chainHeadInfoProvider) =>
        api.Config<IOptimismConfig>().SequencerUrl is not null ? NullTxPool.Instance : base.CreateTxPool(chainHeadInfoProvider);
}
