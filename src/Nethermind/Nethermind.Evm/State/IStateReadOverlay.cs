// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>Answers state reads ahead of the scope they are made in: what a prefix of a block wrote, laid over the
/// state the scope stands on, without that state being changed.</summary>
public interface IStateReadOverlay
{
    /// <summary>True when the overlay knows the account; <paramref name="overlaid"/> is then the account as the
    /// overlay sees it, null for one it knows to be gone.</summary>
    bool TryGetAccount(Address address, Account? underlying, out Account? overlaid);

    /// <summary>True supplies a known slot value, including zero after a clear. False requires an underlying read; the out value is ignored.</summary>
    bool TryGetStorage(Address address, in UInt256 index, out UInt256 value);

    /// <summary>Whether the overlay holds any slot of the account, so a read of its storage must reach the overlay
    /// even where the underlying account has none.</summary>
    bool HasStorage(Address address);
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
