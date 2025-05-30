// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public interface IBeaconBlockRootHandler : IHasAccessList
{
    (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec, bool includeStorageCells = true);
    void StoreBeaconRoot(Block block, in BlockExecutionContext blkCtx, IReleaseSpec spec, ITxTracer tracer);
}
