// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    IOptimismSpecHelper specHelper,
    ILogManager logManager) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager)
{
    private const string NonEmptyWithdrawalsList =
        $"{nameof(Block.Withdrawals)} is not an empty list";

    private const string MissingWithdrawalsRootError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is missing";

    private const string UnexpectedWithdrawalsRootError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is not 'keccak256(rlp(empty_string_code))'";

    /// <remarks>
    /// https://specs.optimism.io/protocol/isthmus/exec-engine.html#backwards-compatibility-considerations
    /// </remarks>
    private const string WithdrawalsRootOfEmptyError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is 'keccak256(rlp(empty_string_code))'";

    private const string NonNullWithdrawalsRootError =
        $"{nameof(BlockHeader.WithdrawalsRoot)} is not null";

    protected override bool ValidateWithdrawals(Block block, IReleaseSpec spec, out string? error)
    {
        BlockHeader header = block.Header;

        // From the most recent
        if (specHelper.IsIsthmus(header))
        {
            if (block.Withdrawals is null || block.Withdrawals.Length != 0)
            {
                error = NonEmptyWithdrawalsList;
                return false;
            }

            if (header.WithdrawalsRoot == null)
            {
                error = MissingWithdrawalsRootError;
                return false;
            }

            if (header.WithdrawalsRoot == Keccak.EmptyTreeHash)
            {
                error = WithdrawalsRootOfEmptyError;
                return false;
            }
        }
        else if (specHelper.IsCanyon(header))
        {
            if (header.WithdrawalsRoot != Keccak.EmptyTreeHash)
            {
                error = UnexpectedWithdrawalsRootError;
                return false;
            }
        }
        else
        {
            if (header.WithdrawalsRoot != null)
            {
                error = NonNullWithdrawalsRootError;
                return false;
            }
        }

        error = null;
        return true;
    }
}
