// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    /// <summary>Releases the resources owned by a decoded value.</summary>
    /// <remarks>The caller must own the value exclusively and release it only once.</remarks>
    public static void ReleaseDecoded<T>(T value) where T : ISszCodec<T> => T.ReleaseDecoded(value);
}
