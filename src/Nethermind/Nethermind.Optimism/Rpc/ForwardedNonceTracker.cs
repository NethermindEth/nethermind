// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

/// <summary>
/// The next nonce of an account that <c>eth_sendTransaction</c> signs on a node that forwards to the sequencer, and the
/// per-account lock that keeps sends from one account in order on any node.
/// </summary>
/// <remarks>
/// Such a node has no transaction pool (<c>NullTxPool</c>), so it cannot read a pending nonce. It uses the
/// account's nonce at the head instead, raised past the transactions it has forwarded that the head does not include yet.
/// Only forwards that the sequencer accepted, and that continue the account's nonces without a gap, are counted.
/// A forward is recorded only after the node signed it with a key of its own wallet, so the tracked accounts are at most
/// the wallet's accounts; an entry goes once the head includes the account's forwarded transactions.
/// </remarks>
public sealed class ForwardedNonceTracker(IBlockFinder blockFinder, IStateReader stateReader)
{
    // Guards both dictionaries; held only for the dictionary work, never across an await.
    private readonly Lock _gate = new();

    // The nonce after the last accepted forward of each account.
    private readonly Dictionary<AddressAsKey, ulong> _nextNonces = [];

    // One lock per account with a send running or waiting; removed when the last of them is done.
    private readonly Dictionary<AddressAsKey, SenderLock> _senderLocks = [];

    internal int Count
    {
        get
        {
            lock (_gate)
                return _nextNonces.Count;
        }
    }

    internal int SenderLockCount
    {
        get
        {
            lock (_gate)
                return _senderLocks.Count;
        }
    }

    /// <summary>
    /// Waits until no other send from <paramref name="sender"/> runs, so that its sends get consecutive nonces.
    /// Sends from other accounts do not wait. Dispose the result once the transaction is sent.
    /// </summary>
    public async Task<IDisposable> LockSenderAsync(Address sender)
    {
        SenderLock? senderLock;
        lock (_gate)
        {
            if (!_senderLocks.TryGetValue(sender, out senderLock))
            {
                senderLock = new SenderLock(this, sender);
                _senderLocks[sender] = senderLock;
            }

            senderLock.Users++;
        }

        await senderLock.Semaphore.WaitAsync();
        return senderLock;
    }

    private void Release(SenderLock senderLock)
    {
        lock (_gate)
        {
            senderLock.Semaphore.Release();
            if (--senderLock.Users == 0)
                _senderLocks.Remove(senderLock.Sender);
        }
    }

    /// <summary>
    /// The nonce to fill for <paramref name="sender"/>. The caller holds the lock of <paramref name="sender"/>.
    /// </summary>
    public ulong GetNextNonce(Address sender)
    {
        ulong headNonce = GetHeadNonce(sender);
        lock (_gate)
        {
            if (!_nextNonces.TryGetValue(sender, out ulong forwardedNonce))
                return headNonce;

            if (forwardedNonce > headNonce)
                return forwardedNonce;

            // The head includes every transaction forwarded so far.
            _nextNonces.Remove(sender);
            return headNonce;
        }
    }

    /// <summary>
    /// Records that the sequencer accepted a transaction of <paramref name="sender"/> with <paramref name="nonce"/>.
    /// The caller holds the lock of <paramref name="sender"/>.
    /// </summary>
    public void OnForwarded(Address sender, ulong nonce)
    {
        // A nonce past a gap waits at the sequencer until the gap is filled, so the next nonce does not move.
        if (nonce != GetNextNonce(sender))
            return;

        lock (_gate)
            _nextNonces[sender] = nonce + 1;
    }

    private ulong GetHeadNonce(Address account) =>
        blockFinder.Head?.Header is { } head && stateReader.TryGetAccount(head, account, out AccountStruct state)
            ? state.Nonce
            : 0;

    private sealed class SenderLock(ForwardedNonceTracker tracker, AddressAsKey sender) : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public AddressAsKey Sender => sender;

        // Sends running or waiting on this lock; guarded by the tracker's _gate.
        public int Users { get; set; }

        public void Dispose() => tracker.Release(this);
    }
}
