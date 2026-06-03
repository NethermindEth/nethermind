// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Consensus.Validators;

public interface IBlockValidator : IHeaderValidator, IWithdrawalValidator
{
    bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error);
    bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true);
    bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error);
    bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error);

    /// <summary>Runs the EIP-7805 IL satisfaction check post-execution and stamps the verdict
    /// on <paramref name="suggestedBlock"/>; no-op when IL is gated off.</summary>
    void CheckInclusionList(Block processedBlock, Block suggestedBlock, IWorldState worldState, ProcessingOptions options);
}
