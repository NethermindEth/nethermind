// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Migration;

/// <summary>How far the HalfPath to flat migration has copied, in trie key order.</summary>
/// <remarks>
/// The copy walks the state trie in pre-order, writing a node before its children and an account before its storage trie,
/// so everything up to <see cref="Account"/> and, within that account's storage, up to <see cref="Slot"/> is in flat, as is
/// every node whose path's lower bound the walk has passed.
/// </remarks>
/// <param name="Account">The path of the last account copied; <c>null</c> before any is.</param>
/// <param name="Slot">The path of the last slot of <paramref name="Account"/> copied; <c>null</c> before any is.</param>
public readonly record struct MigrationCursor(ValueHash256? Account, ValueHash256? Slot)
{
    /// <summary>The cursor before anything is copied.</summary>
    public static MigrationCursor Start => default;

    /// <summary>Whether the account at <paramref name="account"/> has been copied.</summary>
    public bool IsCopied(in ValueHash256 account) => Account is { } copied && account.CompareTo(copied) <= 0;

    /// <summary>Whether the slot at <paramref name="slot"/> of the account at <paramref name="account"/> has been copied.</summary>
    public bool IsCopied(in ValueHash256 account, in ValueHash256 slot)
    {
        if (Account is not { } copied) return false;
        int order = account.CompareTo(copied);
        return order < 0 || (order == 0 && Slot is { } copiedSlot && slot.CompareTo(copiedSlot) <= 0);
    }

    /// <summary>Whether the trie node at <paramref name="path"/> has been copied.</summary>
    /// <param name="address">The account path of the storage trie, or <c>null</c> for the state trie.</param>
    /// <param name="path">The path of the node.</param>
    public bool IsCopied(Hash256? address, in TreePath path) =>
        address is null ? IsCopied(path.ToLowerBoundPath()) : IsCopied(address.ValueHash256, path.ToLowerBoundPath());
}
