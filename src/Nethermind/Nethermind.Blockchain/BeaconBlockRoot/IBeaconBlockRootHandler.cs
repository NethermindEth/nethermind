// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.BeaconBlockRoot;
public interface IBeaconBlockRootHandler
{
    void ScheduleSystemCall(Block block);
}
