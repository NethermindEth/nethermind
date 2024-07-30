// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism.CL;

public interface IBeaconApi
{
    // /eth/v2/beacon/blocks/head
    BeaconBlock GetHead();

    // /eth/v1/beacon/blob_sidecars/:slot:
    BlobSidecar[] GetBlobSidecars(int slot);
}

public struct BeaconBlock
{
    public ulong SlotNumber;
    public ulong PayloadNumber;
}

public struct BlobSidecar
{
    public byte[] Blob;
    public byte[] Kzg;
}
