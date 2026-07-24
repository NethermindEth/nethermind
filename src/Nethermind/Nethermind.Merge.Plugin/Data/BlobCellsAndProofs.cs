// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Requested blob cells and their corresponding KZG proofs.</summary>
public class BlobCellsAndProofs
{
    /// <summary>Indicates whether the blob is available to SSZ callers.</summary>
    [JsonIgnore]
    public bool Available { get; init; }

    /// <summary>Cells in ascending requested-index order, with <see langword="null"/> for unavailable requested cells.</summary>
    [JsonPropertyName("blob_cells")]
    public byte[]?[]? BlobCells { get; init; }

    /// <summary>KZG proofs corresponding one-to-one with <see cref="BlobCells"/>.</summary>
    public byte[]?[]? Proofs { get; init; }

    internal BlobCellMask RequestedMask { get; init; }

    /// <summary>Represents an unavailable blob for SSZ encoding.</summary>
    public static BlobCellsAndProofs Unavailable { get; } = new() { Available = false };
}
