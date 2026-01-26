// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.Data;

[SszSerializable]
public class BlobAndProofV2
{
    [SszVector(131072)]
    public byte[]? Blob { get; set; }


    [SszVector(128)]
    public ProofV2[]? Proofs { get; set; }
}

[SszSerializable]
public struct ProofV2
{
    [SszVector(48)]
    public byte[] SszBytes { get; set; }
}

public enum NullableBlobAndProofV2Enum : byte
{
    None = 1,
    BlobAndProofV2 = 2,
}

[SszSerializable]
public struct NullableBlobAndProofV2
{
    public NullableBlobAndProofV2Enum Selector { get; set; }

    public BlobAndProofV2? BlobAndProofV2 { get; set; }
}
