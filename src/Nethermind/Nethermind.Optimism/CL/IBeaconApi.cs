// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Optimism.CL;

public interface IBeaconApi
{
    // /eth/v2/beacon/blocks/head
    Task<BeaconBlock> GetHead();

    // /eth/v2/beacon/blocks/finalized
    Task<BeaconBlock> GetFinalized();

    // /eth/v1/beacon/blob_sidecars/:slot:
    Task<BlobSidecar[]> GetBlobSidecars(ulong slot);
}

public struct BeaconBlock
{
    public ulong SlotNumber;
    public ulong PayloadNumber;
    public Hash256 ExecutionBlockHash;
    public Hash256 BeaconBlockHash;
    public Transaction[] Transactions;
}

public struct BlobSidecar
{
    public byte[] Blob;

    [JsonPropertyName("kzg_commitment")]
    public byte[] KzgCommitment;

    public byte[] BlobVersionedHash;
}
