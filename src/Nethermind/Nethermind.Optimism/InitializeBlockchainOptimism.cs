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

public class InitializeBlockchainOptimism(OptimismNethermindApi api) : InitializeBlockchain(api)
{
    protected override async Task InitBlockchain()
    {
        await base.InitBlockchain();

        api.RegisterTxType<DepositTransactionForRpc>(new OptimismTxDecoder(), Always.Valid);
        api.RegisterTxType<LegacyTransactionForRpc>(new LegacyTxDecoder(allowEmptySignature: true), new OptimismLegacyTxValidator(api.SpecProvider!.ChainId));
    }
}
