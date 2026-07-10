// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Blockchain;

/// <summary>
/// Utility class during bulk loading to prevent processing queue from becoming too large
/// </summary>
public class BlockTreeSuggestPacer : IDisposable
{
    private TaskCompletionSource? _dbBatchProcessed;
    private TaskCompletionSource? _pausedSignal;
    private ulong _blockNumberReachedToUnlock = 0;
    private readonly ulong _stopBatchSize;
    private readonly ulong _resumeBatchSize;
    private readonly IBlockTree _blockTree;

    public BlockTreeSuggestPacer(IBlockTree blockTree, ulong stopBatchSize = 4096, ulong resumeBatchSize = 2048)
    {
        blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        _blockTree = blockTree;
        _stopBatchSize = stopBatchSize;
        _resumeBatchSize = resumeBatchSize;
    }

    /// <summary>
    /// Awaitable that completes when the pacer is paused — either right now or as soon as it
    /// transitions into the paused state. Used by tests to wait deterministically instead of
    /// polling on side-effects.
    /// </summary>
    public Task WaitForPausedAsync(CancellationToken token = default)
    {
        if (_dbBatchProcessed is not null) return Task.CompletedTask;
        TaskCompletionSource signal = LazyInitializer.EnsureInitialized(
            ref _pausedSignal, () => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))!;
        return token.CanBeCanceled
            ? signal.Task.WaitAsync(token)
            : signal.Task;
    }

    private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
    {
        TaskCompletionSource? completionSource = _dbBatchProcessed;
        if (completionSource is null) return;
        if (e.Block.Number < _blockNumberReachedToUnlock) return;

        _dbBatchProcessed = null;
        completionSource.SetResult();
    }

    public async Task WaitForQueue(ulong currentBlockNumber, CancellationToken token)
    {
        ulong currentHeadNumber = _blockTree.Head?.Number ?? 0;
        // Head can transiently overtake the suggestion (parallel-import advance, post-FCU); wrap would pause indefinitely.
        if (currentBlockNumber > currentHeadNumber
            && currentBlockNumber - currentHeadNumber > _stopBatchSize
            && _dbBatchProcessed is null)
        {
            _blockNumberReachedToUnlock = currentBlockNumber - _stopBatchSize + _resumeBatchSize;
            TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dbBatchProcessed = completionSource;
            Interlocked.Exchange(ref _pausedSignal, null)?.TrySetResult();
        }

        if (_dbBatchProcessed is not null)
        {
            await using (token.Register(() => _dbBatchProcessed.TrySetCanceled()))
            {
                await _dbBatchProcessed.Task;
            }
        }
    }

    public void Dispose() => _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
}
