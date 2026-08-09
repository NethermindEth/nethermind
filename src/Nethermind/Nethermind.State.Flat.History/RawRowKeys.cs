// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History;

internal static class RawRowKeys
{
    public static byte[] NextKeyAfter(byte[] key)
    {
        byte[] bound = new byte[key.Length + 1];
        key.CopyTo(bound, 0);
        return bound;
    }
}
