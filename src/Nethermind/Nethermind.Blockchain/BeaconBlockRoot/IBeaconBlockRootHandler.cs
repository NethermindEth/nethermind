// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public interface IBeaconBlockRootHandler
{
    Address? BeaconRootsAddress(Block block, IReleaseSpec spec);
    void StoreBeaconRoot(Block block, IReleaseSpec spec);
}
