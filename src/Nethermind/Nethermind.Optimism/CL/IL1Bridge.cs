// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Facade.Eth;

namespace Nethermind.Optimism.CL;

public interface IL1Bridge
{
    event Action<BeaconBlock, ulong>? OnNewL1Head;
    Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber);
}
