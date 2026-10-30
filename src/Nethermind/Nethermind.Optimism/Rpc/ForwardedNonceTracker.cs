// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

/// <summary>
/// The next nonce of an account that <c>eth_sendTransaction</c> signs on a node that forwards to the sequencer.
/// </summary>
/// <remarks>
/// Such a node has no transaction pool (<c>NullTxPool</c>), so it cannot read a pending nonce. It uses the
/// account's nonce at the head instead, raised past the transactions it has forwarded that the head does not include yet.
/// Only forwards that the sequencer accepted, and that continue the account's nonces without a gap, are counted.
/// </remarks>
public sealed class ForwardedNonceTracker(IBlockFinder blockFinder, IStateReader stateReader, int maxAccounts = ForwardedNonceTracker.DefaultMaxAccounts)
{
    public const int DefaultMaxAccounts = 1024;

    // The nonce after the last accepted forward of each account; guarded by SendLock.
    private readonly Dictionary<AddressAsKey, ulong> _nextNonces = [];

    /// <summary>
    /// Held from filling a nonce until the transaction is sent, so that concurrent sends get consecutive nonces.
    /// </summary>
    public SemaphoreSlim SendLock { get; } = new(1, 1);

    internal int Count => _nextNonces.Count;

    /// <summary>
    /// The nonce to fill for <paramref name="sender"/>. The caller holds <see cref="SendLock"/>.
    /// </summary>
    public ulong GetNextNonce(Address sender)
    {
        ulong headNonce = GetHeadNonce(sender);
        if (!_nextNonces.TryGetValue(sender, out ulong forwardedNonce))
            return headNonce;

        if (forwardedNonce > headNonce)
            return forwardedNonce;

        // The head includes every transaction forwarded so far.
        _nextNonces.Remove(sender);
        return headNonce;
    }

    /// <summary>
    /// Records that the sequencer accepted a transaction of <paramref name="sender"/> with <paramref name="nonce"/>.
    /// The caller holds <see cref="SendLock"/>.
    /// </summary>
    public void OnForwarded(Address sender, ulong nonce)
    {
        // A nonce past a gap waits at the sequencer until the gap is filled, so the next nonce does not move.
        if (nonce != GetNextNonce(sender))
            return;

        if (!_nextNonces.ContainsKey(sender) && _nextNonces.Count >= maxAccounts)
            MakeRoom();

        _nextNonces[sender] = nonce + 1;
    }

    private void MakeRoom()
    {
        List<AddressAsKey>? included = null;
        foreach ((AddressAsKey account, ulong forwardedNonce) in _nextNonces)
        {
            if (forwardedNonce <= GetHeadNonce(account))
                (included ??= []).Add(account);
        }

        if (included is not null)
        {
            foreach (AddressAsKey account in included)
                _nextNonces.Remove(account);
        }

        // Every tracked account still waits for a forwarded transaction: forget one of them.
        if (_nextNonces.Count >= maxAccounts)
        {
            using Dictionary<AddressAsKey, ulong>.Enumerator enumerator = _nextNonces.GetEnumerator();
            enumerator.MoveNext();
            _nextNonces.Remove(enumerator.Current.Key);
        }
    }

    private ulong GetHeadNonce(Address account) =>
        blockFinder.Head?.Header is { } head && stateReader.TryGetAccount(head, account, out AccountStruct state)
            ? state.Nonce
            : 0;
}
