// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

public class BlobCellsAndProofs
{
    public bool Available { get; init; }
    public byte[]?[]? BlobCells { get; init; }
    public byte[]?[]? Proofs { get; init; }
    public static BlobCellsAndProofs Unavailable { get; } = new() { Available = false };
}
