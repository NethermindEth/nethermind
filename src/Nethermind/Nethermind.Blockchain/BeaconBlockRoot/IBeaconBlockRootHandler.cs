// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain.BeaconBlockRoot;
public interface IBeaconBlockRootHandler
{
    void ScheduleSystemCall(Block block, IReleaseSpec spec, IWorldState stateProvider);
}
