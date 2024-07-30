// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism.CL;

public class EthereumBeaconApi : IBeaconApi
{
    public BeaconBlock GetHead()
    {
        throw new System.NotImplementedException();
    }

    public BlobSidecar[] GetBlobSidecars(int slot)
    {
        throw new System.NotImplementedException();
    }
}
