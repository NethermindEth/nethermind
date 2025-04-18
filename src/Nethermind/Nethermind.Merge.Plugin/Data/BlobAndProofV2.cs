// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Merge.Plugin.Data;

public class BlobAndProofV2(Memory<byte> blob, Memory<byte>[] proofs)
{
    public Memory<byte> Blob { get; set; } = blob;
    public Memory<byte>[] Proofs { get; set; } = proofs;
}
