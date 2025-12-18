// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

public record class BlobAndProofV2(byte[] Blob, byte[][] Proofs);
