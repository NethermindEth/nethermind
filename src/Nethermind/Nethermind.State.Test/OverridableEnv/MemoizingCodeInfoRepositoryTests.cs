// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test.OverridableEnv;

public class MemoizingCodeInfoRepositoryTests
{
    private static readonly IReleaseSpec Spec = Substitute.For<IReleaseSpec>();

    private static (MemoizingCodeInfoRepository repository, ResolvedCodeMemo memo, ICodeInfoRepository inner) Build(byte[] code) =>
        Build(() => new CodeInfo(code));

    private static (MemoizingCodeInfoRepository repository, ResolvedCodeMemo memo, ICodeInfoRepository inner) Build(Func<CodeInfo> codeInfo)
    {
        ICodeInfoRepository inner = Substitute.For<ICodeInfoRepository>();
        inner.GetCachedCodeInfo(Arg.Any<Address>(), Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address>())
            .Returns(_ => codeInfo());
        ResolvedCodeMemo memo = new();
        return (new MemoizingCodeInfoRepository(inner, memo), memo, inner);
    }

    private static void Lookup(MemoizingCodeInfoRepository repository, Address address, int times)
    {
        for (int i = 0; i < times; i++) repository.GetCachedCodeInfo(address, true, Spec, out _);
    }

    private static int InnerLookups(ICodeInfoRepository inner) =>
        inner.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(ICodeInfoRepository.GetCachedCodeInfo));

    [Test]
    public void Answers_repeated_lookups_from_the_memo()
    {
        (MemoizingCodeInfoRepository repository, _, ICodeInfoRepository inner) = Build([0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 5);
        Assert.That(InnerLookups(inner), Is.EqualTo(1));
    }

    [Test]
    public void Inserted_code_is_never_answered_from_the_memo_again_in_the_scope()
    {
        (MemoizingCodeInfoRepository repository, _, ICodeInfoRepository inner) = Build([0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 2);
        repository.InsertCode(new byte[] { 0x60, 0x01 }, TestItem.AddressA, Spec);
        Lookup(repository, TestItem.AddressA, 3);
        Assert.That(InnerLookups(inner), Is.EqualTo(4));
        inner.Received(1).InsertCode(Arg.Any<ReadOnlyMemory<byte>>(), TestItem.AddressA, Spec);
    }

    [Test]
    public void A_delegation_drops_the_authority()
    {
        (MemoizingCodeInfoRepository repository, _, ICodeInfoRepository inner) = Build([0x60, 0x00]);
        Lookup(repository, TestItem.AddressB, 2);
        repository.SetDelegation(TestItem.AddressC, TestItem.AddressB, Spec);
        Lookup(repository, TestItem.AddressB, 2);
        Assert.That(InnerLookups(inner), Is.EqualTo(3));
        inner.Received(1).SetDelegation(TestItem.AddressC, TestItem.AddressB, Spec);
    }

    [Test]
    public void Clear_forgets_the_memo_and_the_written_set()
    {
        (MemoizingCodeInfoRepository repository, ResolvedCodeMemo memo, ICodeInfoRepository inner) = Build([0x60, 0x00]);
        Lookup(repository, TestItem.AddressA, 2);
        repository.InsertCode(new byte[] { 0x60, 0x01 }, TestItem.AddressB, Spec);
        memo.Clear();
        Lookup(repository, TestItem.AddressA, 2);
        Lookup(repository, TestItem.AddressB, 2);
        Assert.That(InnerLookups(inner), Is.EqualTo(1 + 1 + 1));
    }

    [Test]
    public void Precompile_addresses_skip_the_memo(
        [Values("0x0000000000000000000000000000000000000001", "0x000000000000000000000000000000000000000a")] string precompile)
    {
        (MemoizingCodeInfoRepository repository, _, ICodeInfoRepository inner) = Build([0x60, 0x00]);
        Lookup(repository, new Address(precompile), 3);
        Assert.That(InnerLookups(inner), Is.EqualTo(3));
    }

    [Test]
    public void Code_resolving_to_a_precompile_is_not_remembered()
    {
        (MemoizingCodeInfoRepository repository, _, ICodeInfoRepository inner) = Build(() => new CodeInfo(Substitute.For<IPrecompile>()));
        Lookup(repository, TestItem.AddressA, 3);

        Assert.That(InnerLookups(inner), Is.EqualTo(3));
    }

    [Test]
    public void Memo_hits_count_as_code_cache_hits()
    {
        Assume.That(ExecutionMetricsFlag.IsActive, Is.True);
        (MemoizingCodeInfoRepository repository, _, _) = Build([0x60, 0x00]);
        long before = Nethermind.Evm.Metrics.CodeDbCache;

        Lookup(repository, TestItem.AddressA, 5);

        // One inner lookup, then four memo hits; other tests may add to the process-wide counter meanwhile.
        Assert.That(Nethermind.Evm.Metrics.CodeDbCache - before, Is.GreaterThanOrEqualTo(4));
    }

    [Test]
    public void Delegation_designators_are_not_remembered()
    {
        byte[] designator = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
        (MemoizingCodeInfoRepository repository, _, ICodeInfoRepository inner) = Build(designator);
        Lookup(repository, TestItem.AddressA, 3);
        Assert.That(InnerLookups(inner), Is.EqualTo(3));
    }
}
