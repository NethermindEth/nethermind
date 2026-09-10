// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>
/// Validates a proposed block the way an import would, minus the committed seals it cannot have yet:
/// header rules, body, then execution on the parent's state in a discarded scope.
/// </summary>
/// <remarks>Besu's <c>QbftBlockValidatorAdaptor</c> with <c>HeaderValidationMode.LIGHT</c>.</remarks>
public sealed class QbftBlockValidatorAdapter(
    IBlockTree blockTree,
    IBlockValidator blockValidator,
    ISpecProvider specProvider,
    IOverridableEnv<QbftBlockValidatorAdapter.ProcessingEnv> processingEnv,
    ILogManager logManager) : IQbftBlockValidator
{
    private const ProcessingOptions ProposalProcessingOptions =
        ProcessingOptions.ReadOnlyChain | ProcessingOptions.IgnoreParentNotOnMainChain | ProcessingOptions.ForceProcessing;

    private readonly ILogger _logger = logManager.GetClassLogger<QbftBlockValidatorAdapter>();

    public BlockValidationResult ValidateBlock(Block block, ReadOnlyBlockAccessList? blockAccessList)
    {
        BlockHeader? parent = blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None, block.Number - 1);
        if (parent is null)
        {
            return BlockValidationResult.Invalid($"Parent {block.ParentHash} of proposed block {block.Number} is unknown.");
        }

        string? error;
        bool valid;
        using (BftSealValidator.EnterProposalValidation())
        {
            valid = blockValidator.ValidateSuggestedBlock(block, parent, out error);
        }

        if (!valid)
        {
            return BlockValidationResult.Invalid(error ?? "Proposed block failed validation.");
        }

        if (block.BlockAccessList is null && blockAccessList is not null)
        {
            block.BlockAccessList = blockAccessList;
        }

        try
        {
            using Scope<ProcessingEnv> scope = processingEnv.BuildAndOverride(parent);
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            scope.Component.BlockProcessor.ProcessOne(block, ProposalProcessingOptions, NullBlockTracer.Instance, spec, CancellationToken.None);
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Proposed block {block.ToString(Block.Format.Short)} failed execution: {e.Message}");
            return BlockValidationResult.Invalid($"Block processing failed: {e.Message}");
        }

        return BlockValidationResult.Valid;
    }

    public record ProcessingEnv(IBlockProcessor BlockProcessor, IWorldState WorldState);
}

/// <summary>Imports a sealed block by suggesting it to the block tree.</summary>
/// <remarks>
/// Besu imports synchronously; here the block tree accepts the block and the main chain thread executes it, as for
/// every other Nethermind block producer. A true result therefore means accepted, not yet executed. That is safe
/// because the proposal was already executed against the parent state during validation, and because the height
/// manager advances on <c>NewChainHeadEvent</c> rather than on this result: if execution nevertheless fails, the
/// round times out and the validators move to the next round.
/// </remarks>
public sealed class QbftBlockImporter(IBlockTree blockTree, ILogManager logManager) : IQbftBlockImporter
{
    private readonly ILogger _logger = logManager.GetClassLogger<QbftBlockImporter>();

    public bool ImportBlock(Block block, ReadOnlyBlockAccessList? blockAccessList)
    {
        if (block.BlockAccessList is null && blockAccessList is not null)
        {
            block.BlockAccessList = blockAccessList;
        }

        AddBlockResult result = blockTree.SuggestBlock(block);
        if (result is AddBlockResult.Added or AddBlockResult.AlreadyKnown)
        {
            return true;
        }

        if (_logger.IsWarn) _logger.Warn($"Block tree rejected sealed QBFT block {block.ToString(Block.Format.Short)}: {result}");
        return false;
    }
}
