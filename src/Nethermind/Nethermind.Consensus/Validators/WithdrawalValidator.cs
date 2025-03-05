// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Messages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Validators;

public class WithdrawalValidator : IWithdrawalValidator
{
    private readonly ILogger _logger;

    public WithdrawalValidator(ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public bool ValidateWithdrawals(Block block, IReleaseSpec spec, out string? error)
    {
        if (spec.WithdrawalsEnabled && block.Withdrawals is null)
        {
            error = BlockErrorMessages.MissingWithdrawals;

            if (_logger.IsWarn) _logger.Warn($"Withdrawals cannot be null in block {block.Hash} when EIP-4895 activated.");

            return false;
        }

        if (!spec.WithdrawalsEnabled && block.Withdrawals is not null)
        {
            error = BlockErrorMessages.WithdrawalsNotEnabled;

            if (_logger.IsWarn) _logger.Warn($"Withdrawals must be null in block {block.Hash} when EIP-4895 not activated.");

            return false;
        }

        if (block.Withdrawals is not null)
        {
            if (!ValidateWithdrawalsHashMatches(block, out Hash256 withdrawalsRoot))
            {
                error = BlockErrorMessages.InvalidWithdrawalsRoot(block.Header.WithdrawalsRoot, withdrawalsRoot);
                if (_logger.IsWarn) _logger.Warn($"Withdrawals root hash mismatch in block {block.ToString(Block.Format.FullHashAndNumber)}: expected {block.Header.WithdrawalsRoot}, got {withdrawalsRoot}");

                return false;
            }
        }

        error = null;

        return true;
    }

    // TODO: make them private
    public static bool ValidateWithdrawalsHashMatches(Block block, out Hash256? withdrawalsRoot) =>
        ValidateWithdrawalsHashMatches(block.Header, block.Body, out withdrawalsRoot);

    public static bool ValidateWithdrawalsHashMatches(BlockHeader header, BlockBody body, out Hash256? withdrawalsRoot)
    {
        if (body.Withdrawals is null)
        {
            withdrawalsRoot = null;
            return header.WithdrawalsRoot is null;
        }

        return (withdrawalsRoot = new WithdrawalTrie(body.Withdrawals).RootHash) == header.WithdrawalsRoot;
    }
}
