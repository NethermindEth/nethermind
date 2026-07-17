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
    private PacingState _pacingState = new();
    private readonly ulong _stopBatchSize;
    private readonly ulong _resumeBatchSize;
    private readonly IBlockTree _blockTree;

    // An unpaused generation latches once it pauses so observers holding it cannot miss a rapid resume.
    private sealed class PacingState(PacingBatch? pacingBatch = null)
    {
        private TaskCompletionSource? _pauseSignal;
        private int _hasPaused;

        public PacingBatch? PacingBatch { get; } = pacingBatch;

        public Task WaitForPauseAsync(CancellationToken token)
        {
            if (Volatile.Read(ref _hasPaused) != 0) return Task.CompletedTask;

            TaskCompletionSource signal = LazyInitializer.EnsureInitialized(
                ref _pauseSignal, static () => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))!;
            if (Volatile.Read(ref _hasPaused) != 0) signal.TrySetResult();

            return token.CanBeCanceled
                ? signal.Task.WaitAsync(token)
                : signal.Task;
        }

        public void SignalPaused()
        {
            Volatile.Write(ref _hasPaused, 1);
            Interlocked.Exchange(ref _pauseSignal, null)?.TrySetResult();
        }
    }

    private sealed class PacingBatch(ulong unlockBlockNumber)
    {
        public ulong UnlockBlockNumber { get; } = unlockBlockNumber;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
        PacingState state = Volatile.Read(ref _pacingState);
        return state.PacingBatch is not null ? Task.CompletedTask : state.WaitForPauseAsync(token);
    }

    private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
    {
        PacingState state = Volatile.Read(ref _pacingState);
        PacingBatch? pacingBatch = state.PacingBatch;
        if (pacingBatch is null || e.Block.Number < pacingBatch.UnlockBlockNumber) return;

        if (Interlocked.CompareExchange(ref _pacingState, new PacingState(), state) == state)
            pacingBatch.Completion.TrySetResult();
    }

    public async Task WaitForQueue(ulong currentBlockNumber, CancellationToken token)
    {
        ulong currentHeadNumber = _blockTree.Head?.Number ?? 0;
        PacingState state = Volatile.Read(ref _pacingState);
        PacingBatch? pacingBatch = state.PacingBatch;

        // Head can transiently overtake the suggestion (parallel-import advance, post-FCU); wrap would pause indefinitely.
        if (currentBlockNumber > currentHeadNumber
            && currentBlockNumber - currentHeadNumber > _stopBatchSize
            && pacingBatch is null)
        {
            PacingBatch newPacingBatch = new(currentBlockNumber - _stopBatchSize + _resumeBatchSize);
            PacingState pausedState = new(newPacingBatch);
            while (pacingBatch is null)
            {
                if (Interlocked.CompareExchange(ref _pacingState, pausedState, state) == state)
                {
                    state.SignalPaused();
                    pacingBatch = newPacingBatch;
                    break;
                }

                state = Volatile.Read(ref _pacingState);
                pacingBatch = state.PacingBatch;
            }
        }

        if (pacingBatch is not null)
        {
            TaskCompletionSource completion = pacingBatch.Completion;
            await using (token.Register(() => completion.TrySetCanceled()))
            {
                await completion.Task;
            }
        }
    }

    public void Dispose() => _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
}
