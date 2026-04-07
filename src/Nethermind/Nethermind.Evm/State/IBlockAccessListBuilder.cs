// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Evm.State;

public interface IBlockAccessListBuilder
{
    public bool IsGenesis { get; set; }
    public bool TracingEnabled { get; set; }
    public bool ParallelExecutionEnabled { get; }
    public void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContract);
public void AddAccountRead(Address address);
}
