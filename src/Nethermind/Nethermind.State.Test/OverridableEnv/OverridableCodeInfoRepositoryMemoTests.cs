// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test.OverridableEnv;

public class OverridableCodeInfoRepositoryMemoTests
{
    private static readonly IReleaseSpec Spec = Substitute.For<IReleaseSpec>();

    private static (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) Build(bool memoize, byte[] code)
    {
        ICodeInfoRepository inner = Substitute.For<ICodeInfoRepository>();
        inner.GetCachedCodeInfo(Arg.Any<Address>(), Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address>())
            .Returns(_ => new CodeInfo(code));
        OverridableCodeInfoRepository repository = new(inner, Substitute.For<IWorldState>()) { MemoizeResolvedCode = memoize };
        return (repository, inner);
    }

    private static void Lookup(OverridableCodeInfoRepository repository, Address address, int times)
    {
        for (int i = 0; i < times; i++) repository.GetCachedCodeInfo(address, true, Spec, out _);
    }

    private static int InnerLookups(ICodeInfoRepository inner) =>
        inner.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(ICodeInfoRepository.GetCachedCodeInfo));

    [Test]
    public void Answers_repeated_lookups_from_the_memo()
    {
        (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) = Build(true, [0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 5);
        Assert.That(InnerLookups(inner), Is.EqualTo(1));
    }

    [Test]
    public void Without_the_flag_every_lookup_goes_to_the_inner_repository()
    {
        (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) = Build(false, [0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 5);
        Assert.That(InnerLookups(inner), Is.EqualTo(5));
    }

    [Test]
    public void Inserted_code_is_never_answered_from_the_memo_again_in_the_scope()
    {
        (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) = Build(true, [0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 2);
        repository.InsertCode(new byte[] { 0x60, 0x01 }, TestItem.AddressA, Spec);
        Lookup(repository, TestItem.AddressA, 3);
        Assert.That(InnerLookups(inner), Is.EqualTo(4));
        inner.Received(1).InsertCode(Arg.Any<ReadOnlyMemory<byte>>(), TestItem.AddressA, Spec);
    }

    [Test]
    public void A_delegation_drops_the_authority()
    {
        (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) = Build(true, [0x60, 0x00]);
        Lookup(repository, TestItem.AddressB, 2);
        repository.SetDelegation(TestItem.AddressC, TestItem.AddressB, Spec);
        Lookup(repository, TestItem.AddressB, 2);
        Assert.That(InnerLookups(inner), Is.EqualTo(3));
    }

    [Test]
    public void Reset_clears_the_memo_and_the_written_set()
    {
        (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) = Build(true, [0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 2);
        repository.InsertCode(new byte[] { 0x60, 0x01 }, TestItem.AddressB, Spec);
        repository.ResetOverrides();
        Lookup(repository, TestItem.AddressA, 2);
        Lookup(repository, TestItem.AddressB, 2);
        Assert.That(InnerLookups(inner), Is.EqualTo(1 + 1 + 1));
    }

    [Test]
    public void Delegation_designators_are_not_remembered()
    {
        byte[] designator = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
        (OverridableCodeInfoRepository repository, ICodeInfoRepository inner) = Build(true, designator);
        Lookup(repository, TestItem.AddressA, 3);
        Assert.That(InnerLookups(inner), Is.EqualTo(3));
    }
}
