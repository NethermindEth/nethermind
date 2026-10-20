// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// Deterministic draws: the same key gives the same answer wherever and whenever it is drawn, so a run's sample and
/// holdout are the same in the selection step, the replay and the tests.
/// </summary>
public static class Sampling
{
    public const double SampleRate = 0.02;

    public static bool IsSampled(string key, double rate = SampleRate)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash) / 18446744073709551616.0 < rate;
    }
}
