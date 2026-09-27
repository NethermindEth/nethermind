// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>Answers state reads ahead of the scope they are made in: what a prefix of a block wrote, laid over the
/// state the scope stands on, without that state being changed.</summary>
public interface IStateReadOverlay
{
    /// <summary>Stands in for the storage root of an account the overlay knows holds slots but whose real root it
    /// does not have. It only ever replaces the empty root, never the reverse, so that a reader asking whether
    /// there is storage to clear is never told there is none when there is; nothing on the trace path uses the
    /// root for anything else.</summary>
    static readonly Hash256 NonEmptyStorageRoot = Keccak.Compute("state read overlay");

    /// <summary>True when the overlay knows the account; <paramref name="overlaid"/> is then the account as the
    /// overlay sees it, null for one it knows to be gone.</summary>
    bool TryGetAccount(Address address, Account? underlying, out Account? overlaid);

    /// <summary>The same answer for the accounts the overlay can answer for on its own, so that reading one costs no
    /// read of the state underneath. False means the answer needs that state as its basis, and
    /// <see cref="TryGetAccount"/> is asked with it.</summary>
    bool TryGetAccountWithoutBasis(Address address, out Account? overlaid)
    {
        overlaid = null;
        return false;
    }

    /// <summary>True supplies a known slot value, including zero after a clear. False requires an underlying read; the out value is ignored.</summary>
    bool TryGetStorage(Address address, in UInt256 index, out UInt256 value);

    /// <summary>Whether the overlay holds any slot of the account, so a read of its storage must reach the overlay
    /// even where the underlying account has none.</summary>
    bool HasStorage(Address address);

    /// <summary>The bytes of code the overlay itself carries, for a code hash an overlaid account points at that the
    /// code database need not hold: code a prefix deployed and replaced within one block may never have been
    /// written there. Asked only when the code database misses.</summary>
    bool TryGetCode(in ValueHash256 codeHash, [NotNullWhen(true)] out byte[]? code)
    {
        code = null;
        return false;
    }
}

/// <summary>The overlay armed for the scope in flight, if any. One per read-only processing environment.</summary>
public sealed class StateReadOverlaySlot
{
    private IStateReadOverlay? _current;
    private IDisposable? _lease;
    private BlockReadCache? _cache;

    /// <summary>The borrowed overlay for the current scope; null when disarmed.</summary>
    public IStateReadOverlay? Current => _current;

    /// <summary>Reads of the state underneath the overlay, shared by every transaction armed from the same block.</summary>
    public BlockReadCache? Cache => _cache;

    /// <summary>Takes ownership of the lease, replacing and releasing any previous one.</summary>
    public void Arm(IStateReadOverlay overlay, IDisposable lease, BlockReadCache? cache = null)
    {
        Disarm();
        _current = overlay;
        _lease = lease;
        _cache = cache;
    }

    /// <summary>Clears the overlay and releases its lease. Repeated calls are harmless.</summary>
    public void Disarm()
    {
        _current = null;
        _cache = null;
        _lease?.Dispose();
        _lease = null;
    }
}
