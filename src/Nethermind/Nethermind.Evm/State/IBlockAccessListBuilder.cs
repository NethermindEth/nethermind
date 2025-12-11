// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.State;

public interface IBlockAccessListBuilder
{
    public bool TracingEnabled { get; set; }
    public bool ParallelExecutionEnabled { get; }
    public BlockAccessList GeneratedBlockAccessList { get; set; }
    public void ApplyStateChanges(IReleaseSpec spec, bool shouldComputeStateRoot);
    public void SetupGeneratedAccessLists(int txCount);
    public void GenerateBlockAccessList();
    public void AddAccountRead(Address address, int? blockAccessIndex = null);
}