// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    public void Repeat_call_with_same_head_resolves_spec_once_and_returns_it()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        (ISpecProvider specProvider, IBlockFinder blockFinder) = SetupForSingleHeader(header, Cancun.Instance);
        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        for (int i = 0; i < 100; i++)
        {
            Assert.That(provider.GetCurrentHeadSpec(), Is.SameAs(Cancun.Instance));
        }

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

        Assert.That(provider.GetCurrentHeadSpec(), Is.SameAs(Cancun.Instance));
        Assert.That(provider.GetCurrentHeadSpec(), Is.SameAs(Prague.Instance));
    }

    [Test]
    public void Null_header_falls_back_to_zero_fork_activation()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec((ForkActivation)0L).Returns(Cancun.Instance);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns((BlockHeader?)null);

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        Assert.That(provider.GetCurrentHeadSpec(), Is.SameAs(Cancun.Instance));
    }

    [Test]
    public async Task Concurrent_callers_on_stable_head_observe_consistent_spec()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        (ISpecProvider specProvider, IBlockFinder blockFinder) = SetupForSingleHeader(header, Cancun.Instance);
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
                    Assert.That(provider.GetCurrentHeadSpec(), Is.SameAs(Cancun.Instance));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Test]
    public async Task Concurrent_callers_during_head_rotation_never_observe_torn_pair()
    {
        // Each header maps to a distinct spec instance, so any returned spec must be one of
        // the configured mappings — a torn (number, spec) pair would surface as either null
        // or an unrecognised reference. Loop the head fast enough that publishes overlap
        // with reads.
        BlockHeader[] headers =
        {
            Build.A.BlockHeader.WithNumber(10).TestObject,
            Build.A.BlockHeader.WithNumber(11).TestObject,
            Build.A.BlockHeader.WithNumber(12).TestObject,
        };
        IReleaseSpec[] specs = { Cancun.Instance, Prague.Instance, Osaka.Instance };

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        HashSet<IReleaseSpec> validSpecs = [];
        for (int i = 0; i < headers.Length; i++)
        {
            specProvider.GetSpec(headers[i]).Returns(specs[i]);
            validSpecs.Add(specs[i]);
        }

        BlockHeader currentHeader = headers[0];
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(_ => Volatile.Read(ref currentHeader));

        ChainHeadSpecProvider provider = new(specProvider, blockFinder);

        using CancellationTokenSource cts = new();
        const int workers = 16;
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    IReleaseSpec spec = provider.GetCurrentHeadSpec();
                    Assert.That(spec, Is.Not.Null);
                    Assert.That(validSpecs.Contains(spec), Is.True);
                }
            });
        }

        Task rotator = Task.Run(async () =>
        {
            for (int round = 0; round < 200; round++)
            {
                Volatile.Write(ref currentHeader, headers[round % headers.Length]);
                await Task.Yield();
            }
            cts.Cancel();
        });

        await Task.WhenAll(tasks);
        await rotator;
    }

    private static (ISpecProvider SpecProvider, IBlockFinder BlockFinder) SetupForSingleHeader(BlockHeader header, IReleaseSpec spec)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(header).Returns(spec);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.FindBestSuggestedHeader().Returns(header);

        return (specProvider, blockFinder);
    }
}
