// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class MergeFinalizationManagerTests
{
    private IManualBlockFinalizationManager _inner;
    private IPoSSwitcher _poSSwitcher;
    private MergeFinalizationManager _manager;

    [SetUp]
    public void Setup()
    {
        _inner = Substitute.For<IManualBlockFinalizationManager>();
        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _manager = new MergeFinalizationManager(_inner, null, _poSSwitcher);
    }

    [Test]
    public void Dispose_unsubscribes_from_PoSSwitcher_TerminalBlockReached()
    {
        _inner.LastFinalizedBlockLevel.Returns(999L);

        _manager.Dispose();

        _poSSwitcher.TerminalBlockReached += Raise.Event();

        Assert.That(_manager.LastFinalizedBlockLevel, Is.Zero,
            "Handler should be unsubscribed — IsPostMerge must remain false after dispose");
    }

    [Test]
    public void Dispose_unsubscribes_from_inner_BlocksFinalized()
    {
        int count = 0;
        _manager.BlocksFinalized += (_, _) => count++;

        _inner.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(null!, null!));
        Assert.That(count, Is.EqualTo(1), "Event should propagate before Dispose");

        _manager.Dispose();

        _inner.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(null!, null!));
        Assert.That(count, Is.EqualTo(1), "Event should NOT propagate after Dispose");
    }

    [Test]
    public void Double_dispose_does_not_throw()
    {
        Assert.DoesNotThrow(() =>
        {
            _manager.Dispose();
            _manager.Dispose();
        });
    }
}
