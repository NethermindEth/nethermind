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
    private long _blockNumberReachedToUnlock = 0;
    private readonly long _stopBatchSize;
    private readonly long _resumeBatchSize;
    private readonly IBlockTree _blockTree;

    public BlockTreeSuggestPacer(IBlockTree blockTree, long stopBatchSize = 4096, long resumeBatchSize = 2048)
    {
        blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        _blockTree = blockTree;
        _stopBatchSize = stopBatchSize;
        _resumeBatchSize = resumeBatchSize;
    }

    private void BlockTreeOnNewHeadBlock(object sender, BlockEventArgs e)
    {
        TaskCompletionSource? completionSource = _dbBatchProcessed;
        if (completionSource is null) return;
        if (e.Block.Number < _blockNumberReachedToUnlock) return;

        _dbBatchProcessed = null;
        completionSource.SetResult();
    }

    public async Task WaitForQueue(long currentBlockNumber, CancellationToken token)
    {
        long currentHeadNumber = _blockTree.Head?.Number ?? 0;
        if (currentBlockNumber - currentHeadNumber > _stopBatchSize && _dbBatchProcessed is null)
        {
            _blockNumberReachedToUnlock = currentBlockNumber - _stopBatchSize + _resumeBatchSize;
            TaskCompletionSource completionSource = new TaskCompletionSource();
            _dbBatchProcessed = completionSource;
        }

        if (_dbBatchProcessed is not null)
        {
            await using (token.Register(() => _dbBatchProcessed.TrySetCanceled()))
            {
                await _dbBatchProcessed.Task;
            }
        }
    }

    public void Dispose()
    {
        _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;
    }
}
