// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Test.Verkle;

public class VerkleVirtualMachineTestsBase() : VirtualMachineTestsBase(StateType.Verkle)
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;
    protected readonly EthereumEcdsa _ecdsa = new(1, LimboLogs.Instance);
}
