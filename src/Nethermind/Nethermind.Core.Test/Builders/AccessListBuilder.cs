// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Eip2930;

namespace Nethermind.Core.Test.Builders;
public class TestAccessListBuilder : BuilderBase<AccessList>
{
    public TestAccessListBuilder()
    {
        AccessList.Builder accessListBuilder = new();
        foreach (Address address in TestItem.Addresses.Take(5))
        {
            accessListBuilder.AddAddress(address);
        }

        TestObjectInternal = accessListBuilder.Build();
    }
}
