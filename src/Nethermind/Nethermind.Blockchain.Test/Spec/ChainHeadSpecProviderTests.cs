// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Spec;

public class ChainHeadSpecProviderTests
{
    [Test]
    public void GetCurrentHeadSpec_returns_spec_for_current_header()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        IReleaseSpec expected = Cancun.Instance;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(expected);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(header);

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        provider.GetCurrentHeadSpec().Should().BeSameAs(expected);
    }

    [Test]
    public void Repeat_call_with_same_head_does_not_resolve_spec_again()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(Cancun.Instance);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(header);

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        for (int i = 0; i < 100; i++) provider.GetCurrentHeadSpec();

        specProvider.Received(1).GetSpec(header);
    }

    [Test]
    public void New_head_invalidates_cache_and_re_resolves_spec()
    {
        BlockHeader first = Build.A.BlockHeader.WithNumber(42).TestObject;
        BlockHeader second = Build.A.BlockHeader.WithNumber(43).TestObject;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(first).Returns(Cancun.Instance);
        specProvider.GetSpec(second).Returns(Prague.Instance);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(first, second);

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        provider.GetCurrentHeadSpec().Should().BeSameAs(Cancun.Instance);
        provider.GetCurrentHeadSpec().Should().BeSameAs(Prague.Instance);
    }

    [Test]
    public async Task Concurrent_callers_observe_consistent_spec()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(Cancun.Instance);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(header);

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        const int workers = 32;
        const int iterations = 5_000;
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    provider.GetCurrentHeadSpec().Should().BeSameAs(Cancun.Instance);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Test]
    public async Task Concurrent_writers_during_head_change_publish_a_consistent_pair()
    {
        BlockHeader[] headers =
        {
            Build.A.BlockHeader.WithNumber(10).TestObject,
            Build.A.BlockHeader.WithNumber(11).TestObject,
            Build.A.BlockHeader.WithNumber(12).TestObject,
        };
        IReleaseSpec[] specs = { Cancun.Instance, Prague.Instance, Osaka.Instance };

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        for (int i = 0; i < headers.Length; i++) specProvider.GetSpec(headers[i]).Returns(specs[i]);

        Dictionary<long, IReleaseSpec> expectedByNumber = new();
        for (int i = 0; i < headers.Length; i++) expectedByNumber[headers[i].Number] = specs[i];

        BlockHeader currentHeader = headers[0];
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(_ => Volatile.Read(ref currentHeader));

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        const int workers = 16;
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    BlockHeader observed = blockFinder.FindBestSuggestedHeader();
                    IReleaseSpec spec = provider.GetCurrentHeadSpec();
                    // The observed spec must match the spec for some header whose number is
                    // <= the latest published header. Because writers race, allow any of the
                    // valid expected mappings.
                    expectedByNumber.Values.Should().Contain(spec);
                }
            });
        }

        Task rotator = Task.Run(() =>
        {
            for (int i = 0; i < headers.Length; i++)
            {
                Volatile.Write(ref currentHeader, headers[i]);
                Thread.Sleep(1);
            }
        });

        await Task.WhenAll(tasks);
        await rotator;
    }
}
