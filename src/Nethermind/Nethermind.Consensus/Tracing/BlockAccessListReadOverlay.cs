// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>Reads the state before one transaction of a block through the block's access list. Every answer is a
/// lookup and a binary search over the list; nothing is folded or copied, so arming it costs the same for the last
/// transaction of a block as for the second. Slot reads allocate nothing; an account a transaction changed is built
/// once per trace, when the world state first reads it.</summary>
/// <remarks>Its own lease: the list it reads is immutable and shared, so a disarmed slot has nothing to return.</remarks>
internal sealed class BlockAccessListReadOverlay(BlockAccessListPrefix prefix) : IStateReadOverlay, IDisposable
{
    /// <summary>The transaction whose prestate is read; reset between traces by the worker that owns the overlay.</summary>
    public uint TransactionIndex { get; set; }

    public bool TryGetAccount(Address address, Account? underlying, out Account? overlaid) =>
        prefix.TryGetAccount(address, TransactionIndex, underlying, out overlaid);

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value) =>
        prefix.TryGetStorage(address, in index, TransactionIndex, out value);

    public bool HasStorage(Address address) => prefix.HasStorageWrites(address, TransactionIndex);

    public bool TryGetCode(in ValueHash256 codeHash, [NotNullWhen(true)] out byte[]? code) => prefix.TryGetCode(in codeHash, out code);

    public void Dispose()
    {
    }
}
