// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

public record class BlobCellsAndProofsV1([property: JsonPropertyName("blob_cells")] byte[]?[] BlobCells, byte[]?[] Proofs);
