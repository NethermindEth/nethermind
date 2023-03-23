using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Api.Factories;

public interface IBlockProcessorFactory
{
    IBlockProcessor Create(
        ISpecProvider? specProvider,
        IBlockValidator? blockValidator,
        IRewardCalculator? rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor,
        IStateProvider? stateProvider,
        IStorageProvider? storageProvider,
        IReceiptStorage? receiptStorage,
        IWitnessCollector? witnessCollector,
        IWithdrawalProcessor? withdrawalProcessor,
        ILogManager? logManager);
}