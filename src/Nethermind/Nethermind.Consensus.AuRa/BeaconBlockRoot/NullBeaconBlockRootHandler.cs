// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.BeaconBlockRoot;
internal class NullBeaconBlockRootHandler : IBeaconBlockRootHandler
{
    public void ScheduleSystemCall(Block block)
    {
    }

    public static IBeaconBlockRootHandler Instance { get; } = new NullBeaconBlockRootHandler();
}
