// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Facade.Eth;

namespace Nethermind.Optimism.CL;

public interface IL1Bridge
{
    event Action<BlockForRpc, ulong>? OnNewL1Head;
    BlobSidecar[] GetBlobSidecars(int slotNumber);
}
