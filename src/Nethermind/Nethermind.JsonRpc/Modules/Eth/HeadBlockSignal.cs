// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// One subscription on <see cref="IBlockTree.OnUpdateMainChain"/>, shared across all
/// <c>eth_sendRawTransactionSync</c> callers — replaces N-semaphores-per-call.
///
/// <para>Usage: snapshot <see cref="NextHeadTask"/> BEFORE the state check (e.g. receipt lookup).
/// If a head arrives between snapshot and await, the snapshotted Task is already completed and
/// the await returns immediately, so the caller re-checks state on the next iteration. Snapshotting
/// after the check would lose that race.</para>
/// </summary>
/// <remarks>
/// Not <see cref="IBlockTree.NewHeadBlock"/>: it fires before <see cref="IBlockTree.BlockAddedToMain"/>, where the
/// receipt storage publishes the tx index, so a caller woken by it could miss a receipt already in the new head and
/// wait for another block. <see cref="IBlockTree.OnUpdateMainChain"/> fires after every
/// <see cref="IBlockTree.BlockAddedToMain"/> of the branch.
/// </remarks>
public sealed class HeadBlockSignal : IDisposable
{
    private readonly IBlockTree _blockTree;
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HeadBlockSignal(IBlockTree blockTree)
    {
        _blockTree = blockTree;
        _blockTree.OnUpdateMainChain += OnMainChainUpdated;
    }

    public Task NextHeadTask => Volatile.Read(ref _tcs).Task;

    public void Dispose() => _blockTree.OnUpdateMainChain -= OnMainChainUpdated;

    private void OnMainChainUpdated(object? sender, OnUpdateMainChainArgs _)
    {
        TaskCompletionSource prev = Interlocked.Exchange(
            ref _tcs,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        prev.TrySetResult();
    }
}
