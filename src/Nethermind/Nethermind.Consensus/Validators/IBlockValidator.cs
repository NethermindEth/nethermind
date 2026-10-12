// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator, IWithdrawalValidator
{
    bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error);
    /// <param name="validateHashes">
    /// <c>false</c> to skip four keccaks: the header hash, which binds <see cref="BlockHeader.Hash"/> to the header
    /// contents, and the uncles hash, transactions root and withdrawals root, which bind the body to the header.
    /// Everything else still runs, the EIP-4895 withdrawals presence rules included. Validators overriding the
    /// withdrawals validation (Optimism's does) apply their own withdrawals rules regardless of this flag.
    /// <para>
    /// Only pass <c>false</c> after deriving those three roots from the block's own body and verifying the header
    /// hash against the header contents. A payload type that takes the roots off the wire instead - Taiko's is one -
    /// hashes consistently while leaving them unchecked, so the skip is unsound for it. Without the hash check, a
    /// block whose hash does not match its contents is accepted, and that hash is what the block tree and the
    /// consensus layer see.
    /// </para>
    /// </param>
    bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error);
    bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error);
}
