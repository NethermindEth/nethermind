// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.Rpc;
using Nethermind.State;
using NSubstitute;

namespace Nethermind.Optimism.Test.Rpc;

/// <summary>
/// A head that moves forward and back, as in a reorg. At the head with number n every account has nonce n. A read can be
/// held at its next head lookup or after its next state read, to let sends and head changes run in between.
/// </summary>
internal sealed class ReorgingHead
{
    private readonly Block[] _blocks = Enumerable.Range(0, 8).Select(static n => Build.A.Block.WithNumber(n).TestObject).ToArray();
    private int _head = 1;
    private int _holdNextHeadLookup;
    private int _holdNextStateRead;
    private int _stateReads;

    public ReorgingHead()
    {
        Finder.Head.Returns(_ =>
        {
            if (Interlocked.Exchange(ref _holdNextHeadLookup, 0) == 1)
            {
                HeadLookupHeld.TrySetResult();
                ReleaseHeadLookup.Task.Wait(TimeSpan.FromSeconds(30));
            }

            return _blocks[Volatile.Read(ref _head)];
        });
        Reader.TryGetAccount(Arg.Any<BlockHeader?>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(call =>
        {
            if (Failure is { } failure)
                throw failure;

            call[2] = new AccountStruct((ulong)call.ArgAt<BlockHeader>(0).Number, 0);
            AfterStateRead?.Invoke(Interlocked.Increment(ref _stateReads));
            if (Interlocked.Exchange(ref _holdNextStateRead, 0) == 1)
            {
                StateReadHeld.TrySetResult();
                ReleaseStateRead.Task.Wait(TimeSpan.FromSeconds(30));
            }

            return true;
        });
        Tracker = new ForwardedNonceTracker(Finder, Reader);
    }

    public IBlockFinder Finder { get; } = Substitute.For<IBlockFinder>();

    public IStateReader Reader { get; } = Substitute.For<IStateReader>();

    public ForwardedNonceTracker Tracker { get; }

    // Thrown by every state read while set.
    public Exception? Failure { get; set; }

    // Runs after each state read with the number of state reads so far.
    public Action<int>? AfterStateRead { get; set; }

    public int StateReads => Volatile.Read(ref _stateReads);

    public void MoveTo(int nonce) => Volatile.Write(ref _head, nonce);

    /// <summary>The next head lookup sets <see cref="HeadLookupHeld"/>, then waits for <see cref="ReleaseHeadLookup"/>.</summary>
    public void HoldNextHeadLookup() => Volatile.Write(ref _holdNextHeadLookup, 1);

    /// <summary>The next state read sets <see cref="StateReadHeld"/> once it has read, then waits for <see cref="ReleaseStateRead"/>.</summary>
    public void HoldNextStateRead() => Volatile.Write(ref _holdNextStateRead, 1);

    public TaskCompletionSource HeadLookupHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseHeadLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StateReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseStateRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseAll()
    {
        ReleaseHeadLookup.TrySetResult();
        ReleaseStateRead.TrySetResult();
    }
}
