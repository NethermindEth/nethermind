// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

public record class BlobCellsAndProofsV1(byte[]?[] BlobCells, byte[]?[] Proofs);
