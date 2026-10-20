// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableItemReserveTests
{
    [Test]
    public void Reserve_covers_the_index_contract_and_one_root_per_level([Values] bool contractAlreadyInList)
    {
        OverridableReleaseSpec spec = new(London.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = TestItem.AddressF
        };
        GeneratedBlockAccessList blockAccessList = new();
        if (contractAlreadyInList)
        {
            BlockAccessListAtIndex atIndex = new();
            atIndex.AddAccountRead(TestItem.AddressF);
            blockAccessList.Merge(atIndex);
        }

        long reserve = BlockAccessListItemReserve.Calculate(Build.A.Block.TestObject, spec, Substitute.For<IWorldState>(), blockAccessList);

        Assert.That(reserve, Is.EqualTo(Eip8304Constants.TableSizes.Length + (contractAlreadyInList ? 0 : 1)));
    }
}
