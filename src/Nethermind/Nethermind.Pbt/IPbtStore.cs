// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Backing store used by <see cref="TrieUpdater"/> for canonical complete-key mutations.</summary>
/// <remarks>
/// Reads observe the current tree. <see cref="Apply"/> publishes all leaf and node changes together;
/// implementations must leave the store unchanged when it throws.
/// </remarks>
public interface IPbtStore
{
    /// <summary>Gets the encoded canonical node at <paramref name="locator"/>, or <see langword="null"/> when absent.</summary>
    byte[]? GetNode(PbtNodeLocator locator);

    /// <summary>Atomically applies leaf and node mutations for <paramref name="newRoot"/>.</summary>
    void Apply(in ValueHash256 newRoot, IReadOnlyList<PbtLeafMutation> leaves, IReadOnlyList<PbtNodeMutation> nodes);
}

/// <summary>A complete-key leaf replacement; a null value deletes the key.</summary>
public readonly record struct PbtLeafMutation(PbtFullKey Key, ValueHash256? Value);

/// <summary>A canonical node replacement; a null encoding deletes the locator.</summary>
public readonly record struct PbtNodeMutation(PbtNodeLocator Locator, byte[]? Encoding);
