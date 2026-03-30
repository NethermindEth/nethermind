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
    // public BlockAccessList GeneratedBlockAccessList { get; set; }
    // public void ApplyStateChanges(IReleaseSpec spec, bool shouldComputeStateRoot);
    public void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContract);
    // public void SetupGeneratedAccessLists(ILogManager logManager, int txCount);
    // public void MergeIntermediateBalsUpTo(ushort index);
    public void AddAccountRead(Address address);
    // public void LoadSuggestedBlockAccessList(Block suggestedBlock, long gasUsed);
    // public long GasUsed();
    // public void ValidateBlockAccessList(BlockHeader block, ushort index, long gasRemaining, bool validateStorageReads = true);
    // public void SetBlockAccessList(Block block, IReleaseSpec spec);
}
