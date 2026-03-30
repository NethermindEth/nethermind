// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.State;

public interface IBlockAccessListBuilder
{
    public bool TracingEnabled { get; set; }
    public BlockAccessList GeneratedBlockAccessList { get; set; }
    public void AddAccountRead(Address address);
    public void LoadSuggestedBlockAccessList(BlockAccessList suggested, long gasUsed);
    public long GasUsed();
    public void ValidateBlockAccessList(BlockHeader block, ushort index, long gasRemaining, bool isFinal = false);
    public void SetBlockAccessList(Block block, IReleaseSpec spec);
}
