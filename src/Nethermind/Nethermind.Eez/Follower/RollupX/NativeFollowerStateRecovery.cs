// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.State;

namespace Nethermind.Eez.Follower.RollupX;

/// <summary>Restores a stored native checkpoint without publishing intermediate forkchoices.</summary>
internal sealed class NativeFollowerStateRecovery(
    IBlockTree blockTree,
    IStateReader state,
    IBlockProcessingQueue queue,
    IBlockValidator validator,
    IDerivedHeaderRules headerRules,
    IUnsafeBlockPreparation preparation,
    IReceiptConfig receipts) : IFollowerStateRecovery
{
    private Hash256? _target;
    private BlockHeader? _search;
    private BlockHeader? _parent;

    /// <inheritdoc/>
    public async Task Restore(BlockHeader endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (state.HasStateForBlock(endpoint)) return;
        await queue.WaitForBlockProcessing(token);
        if (_target != endpoint.Hash)
        {
            _target = endpoint.Hash;
            _search = endpoint;
            _parent = null;
        }
        if (_parent is not null && (!blockTree.IsMainChain(_parent) || !state.HasStateForBlock(_parent)))
        {
            _search = endpoint;
            _parent = null;
        }
        for (int walked = 0; _parent is null && walked < UnsafeHeadFollower.MaxDepth; walked++)
        {
            token.ThrowIfCancellationRequested();
            BlockHeader current = _search!;
            if (state.HasStateForBlock(current))
            {
                _parent = current;
                break;
            }
            if (current.Number == 0)
                throw new EezFollowerException("Native checkpoint recovery has no retained genesis state.");
            _search = blockTree.FindHeader(current.ParentHash!)
                ?? throw new EezEngineUnavailableException("Native checkpoint recovery is missing a stored ancestor.");
        }
        if (_parent is null)
            throw new EezEngineUnavailableException("Native checkpoint recovery continues its retained-state search on the next poll.");

        for (int restored = 0; _parent.Number < endpoint.Number && restored < UnsafeHeadFollower.MaxBlocksPerAdvance; restored++)
        {
            token.ThrowIfCancellationRequested();
            if (!blockTree.IsMainChain(endpoint))
                throw new EezEngineUnavailableException("The native recovery checkpoint left the canonical chain.");
            Block block = blockTree.FindBlock(_parent.Number + 1, BlockTreeLookupOptions.RequireCanonical)
                ?? throw new EezEngineUnavailableException("Native checkpoint recovery is missing a stored block body.");
            if (block.ParentHash != _parent.Hash)
                throw new EezEngineUnavailableException("The native recovery ancestry changed.");
            if (headerRules.Mismatch(block, _parent) is { } field)
                throw new EezFollowerException($"Stored native recovery block {block.Number} has {field}.");
            if (await preparation.Prepare(_parent, block, token) != UnsafeBlockPreparationResult.Ready)
                throw new L1SourceIncompleteException(block.Number, "Stored native recovery block disagrees with canonical L1 history.");
            if (!validator.ValidateSuggestedBlock(block, _parent, out string? error, validateHashes: true))
                throw new EezFollowerException($"Stored native recovery block {block.Number} is invalid: {error}");
            token.ThrowIfCancellationRequested();
            await Replay(block, token);
            _parent = block.Header;
        }
        if (_parent.Hash != endpoint.Hash)
            throw new EezEngineUnavailableException("Native checkpoint recovery continues on the next poll.");
    }

    private async Task Replay(Block block, CancellationToken token)
    {
        TaskCompletionSource<BlockRemovedEventArgs> verdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Removed(object? sender, BlockRemovedEventArgs result)
        {
            if (result.BlockHash == block.Hash) verdict.TrySetResult(result);
        }
        queue.BlockRemoved += Removed;
        try
        {
            ProcessingOptions options = ProcessingOptions.ForceProcessing | ProcessingOptions.EthereumMerge;
            if (receipts.StoreReceipts) options |= ProcessingOptions.StoreReceipts;
            await queue.Enqueue(block, options);
            // Drain the queued execution before cancellation can invalidate its native L1 evidence.
            await queue.WaitUntilRemovedAsync(block.Hash!);
            token.ThrowIfCancellationRequested();
            if (!verdict.Task.IsCompletedSuccessfully)
                throw new EezEngineUnavailableException("Native checkpoint replay left the queue without a processing verdict.");
            BlockRemovedEventArgs result = await verdict.Task;
            if (result.ProcessingResult == ProcessingResult.ProcessingError)
                throw new EezFollowerException($"Stored native recovery block {block.Number} failed execution: {result.Message}");
            if (result.ProcessingResult != ProcessingResult.Success || !state.HasStateForBlock(block.Header))
                throw new EezEngineUnavailableException($"Native recovery block {block.Number} has no committed state: {result.ProcessingResult}.");
        }
        finally
        {
            queue.BlockRemoved -= Removed;
        }
    }
}
