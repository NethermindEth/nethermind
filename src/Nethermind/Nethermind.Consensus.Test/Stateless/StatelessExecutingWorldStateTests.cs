// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class StatelessExecutingWorldStateTests
{
    /// <summary>Code loaded by hash after a bytecode access check must be the code stored under that hash.</summary>
    /// <remarks>The check remembers the code it loaded; a load of any other hash must still reach the store.</remarks>
    [Test]
    public void Code_by_hash_after_a_bytecode_check_is_the_stored_code([Values] bool loadCheckedCodeFirst)
    {
        byte[] checkedCode = [0x60, 0x01];
        byte[] otherCode = [0x60, 0x02];
        ValueHash256 checkedHash = ValueKeccak.Compute(checkedCode);
        ValueHash256 otherHash = ValueKeccak.Compute(otherCode);

        IWorldState inner = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = inner.BeginScope(IWorldState.PreGenesis);
        inner.CreateAccount(TestItem.AddressA, 0);
        inner.InsertCode(TestItem.AddressA, checkedCode, Prague.Instance);
        inner.CreateAccount(TestItem.AddressB, 0);
        inner.InsertCode(TestItem.AddressB, otherCode, Prague.Instance);

        StatelessExecutingWorldState state = new(inner);
        state.RecordBytecodeAccess(TestItem.AddressA);
        state.RecordBytecodeAccess(TestItem.AddressC);

        using (Assert.EnterMultipleScope())
        {
            if (loadCheckedCodeFirst)
                Assert.That(state.GetCode(in checkedHash).ToArray(), Is.EqualTo(checkedCode), "checked code");
            Assert.That(state.GetCode(in otherHash).ToArray(), Is.EqualTo(otherCode), "other code");
            Assert.That(state.GetCode(in checkedHash).ToArray(), Is.EqualTo(checkedCode), "checked code");
        }
    }
}
