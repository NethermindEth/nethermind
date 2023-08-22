// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.BeaconBlockRoot;
internal class NullBeaconBlockRootHandler : IBeaconBlockRootHandler
{
    public void ScheduleSystemCall(Block block)
    {
    }

    public static IBeaconBlockRootHandler Instance { get; } = new NullBeaconBlockRootHandler();
}
