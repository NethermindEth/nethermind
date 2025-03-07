// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Optimism.CL;

public interface IBeaconApi
{
    // /eth/v1/beacon/blob_sidecars/:slot:
    Task<BlobSidecar[]?> GetBlobSidecars(ulong slot, int indexFrom, int indexTo);
}

public struct BlobSidecar
{
    public byte[] Blob;

    [JsonPropertyName("kzg_commitment")]
    public byte[] KzgCommitment;

    public byte[] BlobVersionedHash;
}
