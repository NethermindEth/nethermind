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
/// https://github.com/ethereum-optimism/specs/blob/main/specs/protocol/isthmus/exec-engine.md#l2tol1messagepasser-storage-root-in-header
/// </summary>
public static class OptimismWithdrawals
{
    private static readonly Hash256 PostCanyonWithdrawalsRoot = Keccak.OfAnEmptySequenceRlp;

    private static class ErrorMessages
    {
        public const string MissingWithdrawalsRoot = $"{nameof(BlockHeader.WithdrawalsRoot)} is missing";

        public const string WithdrawalsRootShouldBeOfEmptyString =
            $"{nameof(BlockHeader.WithdrawalsRoot)} should be keccak256(rlp(empty_string_code))";

        public const string WithdrawalsRootShouldBeNull = $"{nameof(BlockHeader.WithdrawalsRoot)} should be null";
    }

    public static bool Validate(IOptimismSpecHelper specHelper, BlockHeader header, out string? error)
    {
        // From the most recent
        if (specHelper.IsIsthmus(header))
        {
            if (header.WithdrawalsRoot == null)
            {
                error = ErrorMessages.MissingWithdrawalsRoot;
                return false;
            }

            // The withdrawals root should be checked only after the state transition. This can't be done before.
            error = null;
            return true;
        }

        if (specHelper.IsCanyon(header))
        {
            if (header.WithdrawalsRoot == null)
            {
                error = ErrorMessages.MissingWithdrawalsRoot;
                return false;
            }

            if (header.WithdrawalsRoot != PostCanyonWithdrawalsRoot)
            {
                error = ErrorMessages.WithdrawalsRootShouldBeOfEmptyString;
                return false;
            }

            error = null;
            return true;
        }

        // prior Canyon
        if (header.WithdrawalsRoot != null)
        {
            error = ErrorMessages.WithdrawalsRootShouldBeNull;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The withdrawals processor for optimism.
    /// </summary>
    /// <remarks>Constructed over the world state so that it can construct the proper withdrawals hash just before committment.</remarks>
    public class Processor : IWithdrawalProcessor
    {
        private readonly IWorldState _state;
        private readonly IOptimismSpecHelper _specHelper;
        private readonly ILogger _logger;

        public Processor(IWorldState state, ILogManager logManager, IOptimismSpecHelper specHelper)
        {
            _state = state;
            _specHelper = specHelper;
            _logger = logManager.GetClassLogger();
        }

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
        {
            var header = block.Header;

            if (header.WithdrawalsRoot != null)
                return;

            if (_specHelper.IsIsthmus(header))
            {
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
            else if (_specHelper.IsCanyon(header))
            {
                header.WithdrawalsRoot = PostCanyonWithdrawalsRoot;
            }
        }
    }
}
