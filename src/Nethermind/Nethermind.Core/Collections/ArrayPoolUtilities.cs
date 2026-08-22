// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Core.Collections;

internal static class ArrayPoolUtilities
{
    internal static int GetPowerOfTwoCapacity(int minimumLength)
    {
        uint capacity = BitOperations.RoundUpToPowerOf2((uint)minimumLength);

        // No larger power-of-two array length is representable by a signed index.
        return capacity <= int.MaxValue ? (int)capacity : minimumLength;
    }
}
