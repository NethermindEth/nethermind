// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Core.Test.Builders;

public class ChainSpecBuilder: BuilderBase<ChainSpec>
{
    private Dictionary<Address, ChainSpecAllocation> _allocations = new();

    public ChainSpecBuilder WithAllocation(Address address, UInt256 ether)
    {
        _allocations[address] = new ChainSpecAllocation(ether);
        return this;
    }

    protected override void BeforeReturn()
    {
        TestObjectInternal = new ChainSpec()
        {
            Parameters = new ChainParameters(),
            Allocations = _allocations,
            Genesis = Build.A.Block.Genesis.TestObject
        };
    }
}
