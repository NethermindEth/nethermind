// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.BeaconBlockRoot;
public class BeaconBlockRootHandler : IBeaconBlockRootHandler
{
    IBeaconRootContract _beaconRootContract { get; set; }
    Address _address = Address.FromNumber(0x0b); // ToDo Address should be configurable
    public BeaconBlockRootHandler(ITransactionProcessor processor)
    {
        _beaconRootContract = new BeaconRootContract(processor, _address);
    }
    public void ScheduleSystemCall(Block block)
    {
        _beaconRootContract.Invoke(block.Header);
    }
}
