// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
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

    public override bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, out string? error)
    {
        if (!ValidateTxRootMatchesTxs(header, toBeValidated, out Hash256? txRoot))
        {
            error = BlockErrorMessages.InvalidTxRoot(header.TxRoot!, txRoot);
            return false;
        }

        if (!ValidateUnclesHashMatches(header, toBeValidated, out _))
        {
            error = BlockErrorMessages.InvalidUnclesHash;
            return false;
        }

        if (!ValidateWithdrawals(header, toBeValidated.Withdrawals is { Length: 0 }, out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    public override bool ValidateBodyAgainstHeader(BlockHeader header, RlpBlockBody rawBody, out string? error)
    {
        try
        {
            if (!ValidateTxRootMatchesTxs(header, rawBody, out Hash256? txRoot))
            {
                error = BlockErrorMessages.InvalidTxRoot(header.TxRoot!, txRoot);
                return false;
            }

            if (!ValidateUnclesHashMatches(header, rawBody, out _))
            {
                error = BlockErrorMessages.InvalidUnclesHash;
                return false;
            }

            // An empty withdrawals list is the single-byte sequence 0xc0.
            if (!ValidateWithdrawals(header, rawBody.WithdrawalsSequence is { Length: 1 }, out error))
            {
                return false;
            }

            error = null;
            return true;
        }
        catch (RlpException e)
        {
            error = e.Message;
            return false;
        }
    }

    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, ref string? error) =>
        // Base implementation validates BlobGasUsed, but Blob transactions are disabled in Optimism since Ecotone
        specHelper.IsEcotone(block.Header) || base.ValidateEip4844Fields(block, spec, ref error);

    protected override bool ValidateWithdrawals(Block block, IReleaseSpec spec, bool validateHashes, ref string? error) =>
        ValidateWithdrawals(block.Header, block.Body.Withdrawals is { Length: 0 }, out error);

    private bool ValidateWithdrawals(BlockHeader header, bool withdrawalsAreEmptyList, out string? error)
    {
        // From the most recent
        if (specHelper.IsIsthmus(header))
        {
            if (!withdrawalsAreEmptyList)
            {
                error = NonEmptyWithdrawalsList;
                return false;
            }

            if (header.WithdrawalsRoot is null)
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
        else if (header.WithdrawalsRoot is not null)
        {
            error = NonNullWithdrawalsRootError;
            return false;
        }

        error = null;
        return true;
    }
}
