// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism;

/// <summary>
/// The withdrawal processor for optimism.
/// </summary>
/// <remarks>
/// Constructed over the world state so that it can construct the proper withdrawals hash just before commitment.
/// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#l2tol1messagepasser-storage-root-in-header
/// </remarks>
public class OptimismWithdrawalProcessor : IWithdrawalProcessor
{
    private readonly IWorldState _state;
    private readonly IOptimismSpecHelper _specHelper;
    private readonly ILogger _logger;

    public OptimismWithdrawalProcessor(IWorldState state, ILogManager logManager, IOptimismSpecHelper specHelper)
    {
        _state = state;
        _specHelper = specHelper;
        _logger = logManager.GetClassLogger();
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        var header = block.Header;

        if (_specHelper.IsIsthmus(header))
        {
            _state.Commit(spec, commitRoots: true);

            if (_state.TryGetAccount(PreDeploys.L2ToL1MessagePasser, out var account))
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
