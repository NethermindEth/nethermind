// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>
/// QBFT blocks are sealed by the state machine with committed seals, so sealing a produced block is a no-op.
/// </summary>
public sealed class BftSealer(ISigner signer) : ISealer
{
    public Address Address => signer.Address;

    public bool CanSeal(ulong blockNumber, Hash256 parentHash) => signer.CanSign;

    public Task<Block> SealBlock(Block block, CancellationToken cancellationToken) => Task.FromResult(block);
}

/// <summary>Sets the processing author before import: the configured beneficiary receives the fees, otherwise the proposer.</summary>
public sealed class BftAuthorRecoveryStep(BftForksSchedule forksSchedule) : IBlockPreprocessorStep
{
    public void RecoverData(Block block)
    {
        BlockHeader header = block.Header;
        header.Author ??= forksSchedule.GetFork((long)header.Number, header.Timestamp).MiningBeneficiary ?? header.Beneficiary;
    }
}

/// <summary>Pays the fork's <c>blockreward</c> to its <c>miningbeneficiary</c> (or the proposer); nothing when the reward is zero.</summary>
public sealed class BftRewardCalculator(BftForksSchedule forksSchedule) : IRewardCalculator, IRewardCalculatorSource
{
    public BlockReward[] CalculateRewards(Block block)
    {
        if (block.IsGenesis)
        {
            return [];
        }

        BftConfigSnapshot config = forksSchedule.GetFork((long)block.Number, block.Timestamp);
        return config.BlockReward.IsZero
            ? []
            : [new BlockReward(config.MiningBeneficiary ?? block.Header.Beneficiary!, config.BlockReward)];
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;
}

/// <summary>Gas limit steering toward <c>Blocks.TargetBlockGasLimit</c>, the QBFT equivalent of Besu's <c>--target-gas-limit</c>.</summary>
public sealed class BftGasLimitCalculator(ISpecProvider specProvider, Nethermind.Config.IBlocksConfig blocksConfig) : IGasLimitCalculator
{
    private readonly TargetAdjustedGasLimitCalculator _inner = new(specProvider, blocksConfig);

    public ulong GetGasLimit(BlockHeader parentHeader, ulong? targetGasLimit = null) => _inner.GetGasLimit(parentHeader, targetGasLimit);
}
