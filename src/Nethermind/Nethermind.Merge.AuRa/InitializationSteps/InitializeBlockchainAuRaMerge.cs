// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa;
using Nethermind.Merge.AuRa.Withdrawals;

namespace Nethermind.Merge.AuRa.InitializationSteps
{
    public class InitializeBlockchainAuRaMerge : InitializeBlockchainAuRa
    {
        private readonly AuRaNethermindApi _api;

        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }
    }
}

public class AuraMergeBlockchainStack: AuraBlockchainStack
{
    public AuraMergeBlockchainStack(INethermindApi api, ITransactionProcessor transactionProcessor) : base(api, transactionProcessor)
    {
    }

    protected override BlockProcessor NewBlockProcessor(AuRaNethermindApi api, ITxFilter txFilter, ContractRewriter contractRewriter)
    {
        var withdrawalContractFactory = new WithdrawalContractFactory(_api.ChainSpec!.AuRa, _api.AbiEncoder);

        return new AuRaMergeBlockProcessor(
            _api.SpecProvider!,
            _api.BlockValidator!,
            _api.RewardCalculatorSource!.Get(_transactionProcessor),
            new BlockProcessor.BlockValidationTransactionsExecutor(_transactionProcessor, _api.WorldState!),
            _api.WorldState!,
            _api.ReceiptStorage!,
            _api.LogManager,
            _api.BlockTree!,
            new AuraWithdrawalProcessor(
                withdrawalContractFactory.Create(_transactionProcessor), _api.LogManager),
            txFilter,
            GetGasLimitCalculator(),
            contractRewriter
        );
    }
}
