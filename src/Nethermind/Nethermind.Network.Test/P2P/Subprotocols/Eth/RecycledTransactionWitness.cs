// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

/// <summary>Observes a protocol handler recycling a transaction, from outside the transaction itself.</summary>
/// <remarks>A recycled transaction belongs to <c>TxDecoder.TxObjectPool</c> from the moment it is returned, and
/// the pool serves the most recently returned instance to the very next rent — in this assembly, a decode on a
/// parallel fixture's thread, which repopulates it within microseconds. Its own fields are therefore not a sound
/// observation of the recycling. This owner is: recycling calls <see cref="Transaction.ClearPreHash"/>, which
/// disposes it, and nothing but the test ever holds a reference to it.</remarks>
internal sealed class RecycledTransactionWitness : IMemoryOwner<byte>
{
    private readonly byte[] _preHash;

    /// <param name="transaction">The transaction to watch, whose pre-hash this owner takes over.</param>
    /// <param name="marker">Pre-hash content, so transactions watched within one test hash differently.</param>
    public RecycledTransactionWitness(Transaction transaction, byte marker)
    {
        _preHash = [marker];
        transaction.SetPreHashMemoryNoLock(_preHash, this);
    }

    /// <summary>Whether the transaction's pre-hash has been released, which recycling does.</summary>
    /// <remarks>Any other pre-hash consumer, notably a <see cref="Transaction.Hash"/> read, releases it too, so assert on
    /// this only where the path under test cannot hash the transaction first.</remarks>
    public bool WasRecycled { get; private set; }

    public Memory<byte> Memory => _preHash;

    public void Dispose() => WasRecycled = true;
}
