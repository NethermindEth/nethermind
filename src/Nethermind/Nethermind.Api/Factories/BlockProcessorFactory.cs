using System;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Api.Factories;

public class BlockProcessorFactory : IApiComponentFactory<IBlockProcessor>
{
    private readonly INethermindApi _api;

    public BlockProcessorFactory(INethermindApi api)
    {
        _api = api;
    }

    public IBlockProcessor Create()
    {
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.RewardCalculatorSource);
        ArgumentNullException.ThrowIfNull(_api.TransactionProcessor);

        IRewardCalculator rewardCalculator = _api.RewardCalculatorSource.Get(_api.TransactionProcessor);

        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor =
            new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor, _api.StateProvider!);

        return new BlockProcessor(
            _api.SpecProvider,
            _api.BlockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            _api.StateProvider,
            _api.StorageProvider,
            _api.ReceiptStorage,
            _api.WitnessCollector,
            _api.LogManager);
    }
}
