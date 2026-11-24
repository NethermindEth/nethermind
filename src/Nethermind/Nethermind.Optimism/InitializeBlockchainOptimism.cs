// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Consensus.Validators;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Init.Steps;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp.TxDecoders;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism(INethermindApi api) : InitializeBlockchain(api)
{
    private readonly INethermindApi _api = api;

    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        _api.RegisterTxType<DepositTransactionForRpc>(new OptimismTxDecoder(), Always.Valid);
        _api.RegisterTxType<LegacyTransactionForRpc>(new LegacyTxDecoder(allowEmptySignature: true), new OptimismLegacyTxValidator(_api.SpecProvider!.ChainId));
    }
}
