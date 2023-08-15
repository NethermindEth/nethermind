// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.BeaconBlockRoot;
public interface IBeaconBlockRootHandler
{
    void ScheduleSystemCall(Block block);
}
