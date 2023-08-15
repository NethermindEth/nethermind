// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.Precompiles.Stateful;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.BeaconBlockRoot;
public class BeaconBlockRootHandler : IBeaconBlockRootHandler
{
    IBeaconRootContract _beaconRootContract { get; set; }
    Address _address = new Address("0x0b");
    public BeaconBlockRootHandler(ITransactionProcessor processor)
    {
        _beaconRootContract = new BeaconRootContract(processor, _address);
    }
    public void ScheduleSystemCall(Block block)
    {
        _beaconRootContract.Invoke(block.Header);
    }
}
