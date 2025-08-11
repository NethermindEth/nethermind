// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

public interface IKademiliaHash<THash>
{
    static abstract THash Zero { get; }

    static abstract int CalculateLogDistance(THash h1, THash h2);

    static abstract int Compare(THash a, THash b, THash c);

    static abstract THash XorDistance(THash hash1, THash hash2);
    static abstract THash GetRandomHashAtDistance(THash currentHash, int distance);
    static abstract THash GetRandomHashAtDistance(THash currentHash, int distance, Random random);
    static abstract THash CopyForRandom(THash currentHash, THash randomizedHash, int distance);

    static abstract int MaxDistance { get; }
    byte[] Bytes { get; }

    static abstract THash FromBytes(byte[] bytes);
}
