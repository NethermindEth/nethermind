// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Store.Test.OverridableEnv;

public class ResolvedCodeMemoTests
{
    private static readonly CodeInfo CodeA = new(new byte[] { 0x60, 0x01 });
    private static readonly CodeInfo CodeB = new(new byte[] { 0x60, 0x02 });

    [Test]
    public void Repeated_lookup_answers_the_remembered_code()
    {
        ResolvedCodeMemo memo = new();
        memo.Remember(TestItem.AddressA, CodeA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(memo.TryGet(TestItem.AddressA, out CodeInfo first), Is.True);
            Assert.That(first, Is.SameAs(CodeA));
            Assert.That(memo.TryGet(new Address(TestItem.AddressA.Bytes), out CodeInfo repeat), Is.True);
            Assert.That(repeat, Is.SameAs(CodeA));
            Assert.That(memo.TryGet(TestItem.AddressB, out _), Is.False);
        }
    }

    [Test]
    public void Remembering_the_last_address_again_replaces_its_code()
    {
        ResolvedCodeMemo memo = new();
        memo.Remember(TestItem.AddressA, CodeA);
        memo.TryGet(TestItem.AddressA, out _);

        memo.Remember(TestItem.AddressA, CodeB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(memo.TryGet(TestItem.AddressA, out CodeInfo code), Is.True);
            Assert.That(code, Is.SameAs(CodeB));
        }
    }

    [Test]
    public void Forgetting_or_clearing_drops_the_last_address()
    {
        ResolvedCodeMemo memo = new();
        memo.Remember(TestItem.AddressA, CodeA);
        memo.TryGet(TestItem.AddressA, out _);

        memo.Forget(TestItem.AddressA);
        Assert.That(memo.TryGet(TestItem.AddressA, out _), Is.False);

        memo.Clear();
        memo.Remember(TestItem.AddressB, CodeB);
        memo.TryGet(TestItem.AddressB, out _);
        memo.Clear();
        Assert.That(memo.TryGet(TestItem.AddressB, out _), Is.False);
    }
}
