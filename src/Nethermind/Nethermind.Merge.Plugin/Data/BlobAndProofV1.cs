// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.Data;

public class BlobAndProofV1(byte[] blob, byte[] proof)
{
    public byte[] Blob { get; set; } = blob;
    public byte[] Proof { get; set; } = proof;
}
