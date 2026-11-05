// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>The single place the Engine API cell-bitfield bit convention lives.</summary>
internal static class BlobCellBits
{
    /// <summary>Packs a bitfield of exactly <see cref="BlobCellMask.CellCount"/> bits into its mask.</summary>
    /// <remarks><see cref="BitArray.CopyTo(System.Array, int)"/> already emits the wire layout: bit <c>i</c>
    /// is bit <c>i % 8</c> of byte <c>i / 8</c>.</remarks>
    /// <param name="bits">The bitfield, which callers must have length-checked.</param>
    internal static BlobCellMask ToMask(BitArray bits)
    {
        byte[] bytes = new byte[BlobCellMask.FixedByteLength];
        bits.CopyTo(bytes, 0);
        return BlobCellMask.FromBytes(bytes);
    }
}
