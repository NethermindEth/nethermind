// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.AuRa.BeaconBlockRoot;
internal class NullBeaconBlockRootHandler : IBeaconBlockRootHandler
{
    public static IBeaconBlockRootHandler Instance { get; } = new NullBeaconBlockRootHandler();
    public void ScheduleSystemCall(Block block, IReleaseSpec spec)
    {
    }
}
