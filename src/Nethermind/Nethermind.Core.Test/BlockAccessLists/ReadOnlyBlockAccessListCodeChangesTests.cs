// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class ReadOnlyBlockAccessListCodeChangesTests
{
    [Test]
    public void CodeChangesByHash_returns_null_when_no_code_changes()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(0).TestObject)
            .TestObject;

        Assert.That(bal.CodeChangesByHash, Is.Null);
    }

    [Test]
    public void CodeChangesByHash_maps_earliest_index_and_is_cached()
    {
        byte[] code = [0x60, 0x00];
        ValueHash256 codeHash = ValueKeccak.Compute(code);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithCodeChanges(new CodeChange(3, code)).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithCodeChanges(new CodeChange(1, code)).TestObject)
            .TestObject;

        Dictionary<ValueHash256, (uint Index, byte[] Code)>? map = bal.CodeChangesByHash;

        Assert.That(map, Is.Not.Null);
        Assert.That(map![codeHash].Index, Is.EqualTo(1u), "earliest declaring tx index wins");
        Assert.That(map[codeHash].Code, Is.SameAs(code));
        // Built once per BAL instance: repeated access returns the cached map.
        Assert.That(bal.CodeChangesByHash, Is.SameAs(map));
    }
}
