// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool.Collections;

/// <summary>
/// Blob and single KZG proof for engine_getBlobsV1 (pre-PeerDAS format).
/// </summary>
public readonly record struct BlobAndProofV1(byte[] Blob, byte[] Proof);

/// <summary>
/// Blob and cell proofs for engine_getBlobsV2 (PeerDAS format).
/// </summary>
public readonly record struct BlobAndProofV2(byte[] Blob, byte[][] Proofs);
