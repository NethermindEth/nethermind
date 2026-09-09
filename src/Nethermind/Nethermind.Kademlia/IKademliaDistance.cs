// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Defines Kademlia XOR-distance operations for a consumer-owned key type.
/// </summary>
/// <typeparam name="TKadKey">The key-space value used by the routing table.</typeparam>
public interface IKademliaDistance<TKadKey> where TKadKey : notnull
{
    /// <summary>
    /// The maximum log distance supported by the key space.
    /// </summary>
    int MaxDistance { get; }

    /// <summary>
    /// The all-zero key for the key space.
    /// </summary>
    TKadKey Zero { get; }

    /// <summary>
    /// Returns the XOR log distance between <paramref name="left"/> and <paramref name="right"/>.
    /// </summary>
    int CalculateLogDistance(TKadKey left, TKadKey right);

    /// <summary>
    /// Compares two keys by XOR distance to <paramref name="target"/>.
    /// </summary>
    int Compare(TKadKey left, TKadKey right, TKadKey target);

    /// <summary>
    /// Returns whether the bit at <paramref name="index"/> is set.
    /// </summary>
    bool GetBit(TKadKey key, int index);

    /// <summary>
    /// Returns a key with the bit at <paramref name="index"/> set.
    /// </summary>
    TKadKey SetBit(TKadKey key, int index);
}
