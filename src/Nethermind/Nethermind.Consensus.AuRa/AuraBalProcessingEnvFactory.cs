// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.BlockLevelAccessList;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// BAL env factory for the AuRa block producer. Mirrors the manual mainnet factory but wraps the
/// withdrawal processor in <see cref="BlockProductionWithdrawalProcessor"/> so the produced block's
/// <c>WithdrawalsRoot</c> is set. The DI path gets this via the block-producer scope's
/// IWithdrawalProcessor decorator; the AuRa producer builds its BAL manager manually, so it needs
/// the wrap applied here instead.
/// </summary>
public sealed class AuraBalProcessingEnvFactory(
    IBlockhashProvider blockHashProvider,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    ILogManager logManager,
    ITransactionProcessorFactory txProcessorFactory,
    CodeInfoRepositoryFactory codeInfoRepositoryFactory,
    IWithdrawalProcessorFactory withdrawalProcessorFactory) : IBalProcessingEnvFactory
{
    public IBalProcessingEnv Create(bool parallel)
    {
        if (parallel)
        {
            BlockAccessListBasedWorldState balWorldState = new(stateProvider, logManager);
            TracedAccessWorldState worldState = new(balWorldState, parallel: true);
            VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(worldState);
            ITransactionProcessor processor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
            return new ParallelBalEnv(balWorldState, worldState, processor, new ExecuteTransactionProcessorAdapter(processor), Withdrawal(worldState, processor));
        }
        else
        {
            TracedAccessWorldState worldState = new(stateProvider, parallel: false);
            VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            ICodeInfoRepository codeInfoRepository = codeInfoRepositoryFactory(worldState);
            ITransactionProcessor processor = txProcessorFactory.Create(BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
            return new SequentialBalEnv(worldState, processor, new ExecuteTransactionProcessorAdapter(processor), Withdrawal(worldState, processor));
        }
    }

    private IWithdrawalProcessor Withdrawal(IWorldState worldState, ITransactionProcessor processor)
        => new BlockProductionWithdrawalProcessor(withdrawalProcessorFactory.Create(worldState, processor));
}
