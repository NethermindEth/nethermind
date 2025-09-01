// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Optimism;

/// <summary>
/// The withdrawal processor for optimism.
/// </summary>
/// <remarks>
/// Constructed over the world state so that it can construct the proper withdrawals hash just before commitment.
/// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#l2tol1messagepasser-storage-root-in-header
/// </remarks>
public class OptimismWithdrawalProcessor(IWorldState state, ILogManager logManager, IOptimismSpecHelper specHelper) : IWithdrawalProcessor
{
    private readonly IWorldState _state = state;
    private readonly IOptimismSpecHelper _specHelper = specHelper;
    private readonly ILogger _logger = logManager.GetClassLogger();

    public void ProcessWithdrawals(Block block, IReleaseSpec spec, ITxTracer? tracer = null)
    {
        BlockHeader header = block.Header;

        if (_specHelper.IsIsthmus(header))
        {
            _state.Commit(spec, commitRoots: true);

            if (_state.TryGetAccount(PreDeploys.L2ToL1MessagePasser, out AccountStruct account))
            {
                if (_logger.IsDebug)
                    _logger.Debug($"Setting {nameof(BlockHeader.WithdrawalsRoot)} to {account.StorageRoot}");

                header.WithdrawalsRoot = new Hash256(account.StorageRoot);
            }
            else
            {
                header.WithdrawalsRoot = Keccak.EmptyTreeHash;
            }
        }
    }
}

public sealed class OptimismGenesisPostProcessor(
    OptimismWithdrawalProcessor withdrawalProcessor,
    ISpecProvider specProvider
) : IGenesisPostProcessor
{
    public void PostProcess(Block genesis)
    {
        // When Isthmus is enabled at Genesis it's required that we compute the `WithdrawalsRoot` from the L2ToL1MessagePasser account.
        // See: https://specs.optimism.io/protocol/isthmus/exec-engine.html?search=#genesis-block
        withdrawalProcessor.ProcessWithdrawals(genesis, spec: specProvider.GetSpec(genesis.Header));
    }
}
