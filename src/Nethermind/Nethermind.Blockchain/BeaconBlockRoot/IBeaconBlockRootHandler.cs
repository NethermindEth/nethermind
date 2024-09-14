// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public interface IBeaconBlockRootHandler
{
    (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec);
    void StoreBeaconRoot(Block block, IReleaseSpec spec);
}
