// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

public class BlobAndProofV2(byte[] blob, byte[][] proofs)
{
    public byte[] Blob { get; set; } = blob;
    public byte[][] Proofs { get; set; } = proofs;
}
