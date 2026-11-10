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
/// The caller records only transactions the node signed with a key of its own wallet, that the sequencer accepted and
/// that continue the account's nonces without a gap, so the tracked accounts are at most the wallet's accounts.
/// An entry is dropped at the account's next send once the head includes its forwarded transactions.
/// </remarks>
public sealed class ForwardedNonceTracker(IBlockFinder blockFinder, IStateReader stateReader)
{
    // Guards both dictionaries and _entriesVersion; held only briefly and never across an await. Only the fallback of
    // GetPendingNonce holds it across a state read.
    private readonly Lock _gate = new();

    // The nonce after the last accepted forward of each account.
    private readonly Dictionary<AddressAsKey, ulong> _nextNonces = [];

    // Changes with every record or drop in _nextNonces, so that a pending read can tell whether an entry changed while it read
    // the head state. One version serves all accounts: on a busy node a read can retry because another account sent.
    private long _entriesVersion;

    // Pending reads that read the head state without _gate before one holds _gate across that read.
    internal const int OptimisticPendingReads = 3;

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

    // Sends from the account that hold its lock or wait on it, and whether one of them holds it.
    internal (int Users, bool Held) SenderLockState(Address sender)
    {
        lock (_gate)
            return _senderLocks.TryGetValue(sender, out SenderLock? senderLock)
                ? (senderLock.Users, senderLock.Semaphore.CurrentCount == 0)
                : (0, false);
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
    /// The nonce to fill for <paramref name="sender"/>. The caller holds the lock of <paramref name="sender"/>: this call
    /// drops the entry of <paramref name="sender"/> once the head nonce has reached it, and only the holder of the lock
    /// may, since a call that read an older head still needs the entry.
    /// Throws when the head state cannot be read, for example <c>MissingTrieNodeException</c> when it is not available.
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
            _entriesVersion++;
            return headNonce;
        }
    }

    /// <summary>
    /// The pending nonce of <paramref name="sender"/>, for a caller that does not hold the lock of <paramref name="sender"/>:
    /// max(head nonce, entry) at one instant during the call, whatever sends and head changes run meanwhile. It reserves
    /// nothing, so a send can still take it, and it never drops an entry.
    /// Throws when the head state cannot be read, as <see cref="GetNextNonce"/> does.
    /// </summary>
    /// <remarks>
    /// The entry is read under <c>_gate</c> and the head state without it. When no entry changed while the head state was
    /// read, both held at the instant the head was looked up. Otherwise the read is retried, and after
    /// <see cref="OptimisticPendingReads"/> tries it holds <c>_gate</c> across the head state read, which no entry change
    /// can interleave.
    /// </remarks>
    public ulong GetPendingNonce(Address sender)
    {
        for (int attempt = 0; attempt < OptimisticPendingReads; attempt++)
        {
            long version;
            ulong forwardedNonce;
            lock (_gate)
            {
                version = _entriesVersion;
                forwardedNonce = GetForwardedNonce(sender);
            }

            ulong headNonce = GetHeadNonce(sender);
            lock (_gate)
            {
                if (_entriesVersion == version)
                    return Math.Max(forwardedNonce, headNonce);
            }
        }

        lock (_gate)
            return Math.Max(GetForwardedNonce(sender), GetHeadNonce(sender));
    }

    // The nonce after the last accepted forward of the account, or 0 without one. The caller holds _gate.
    private ulong GetForwardedNonce(Address sender) =>
        _nextNonces.TryGetValue(sender, out ulong forwardedNonce) ? forwardedNonce : 0;

    /// <summary>
    /// Records that the sequencer accepted a transaction of <paramref name="sender"/> with <paramref name="nonce"/>, the
    /// nonce <see cref="GetNextNonce"/> gave. The caller holds the lock of <paramref name="sender"/>.
    /// </summary>
    public void OnForwarded(Address sender, ulong nonce)
    {
        lock (_gate)
        {
            _nextNonces[sender] = nonce + 1;
            _entriesVersion++;
        }
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
