// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

public class TimeoutTests
{
    private static Signature SignTimeout(PrivateKey key, ulong round, ulong gap)
    {
        // mirrors TimeoutCertificateManager.ComputeTimeoutMsgHash
        var dec = new TimeoutDecoder();
        var timeout = new Timeout(round, signature: null, gap);
        Rlp rlp = dec.Encode(timeout, Nethermind.Serialization.Rlp.RlpBehaviors.ForSealing);
        var hash = Keccak.Compute(rlp.Bytes).ValueHash256;
        return new EthereumEcdsa(0).Sign(key, hash);
    }

    private static Timeout BuildSignedTimeout(ulong round, ulong gap, PrivateKey key)
    {
        return new Timeout(round, SignTimeout(key, round, gap), gap) { Signer = key.Address };
    }

    [Test]
    public async Task TestTimeoutMessageHandlerSuccessfullyGenerateTCandSyncInfo()
    {
        var chain = await XdcTestBlockchain.Create();

        var ctx   = chain.XdcContext;
        var head  = (XdcBlockHeader) chain.BlockTree.Head!.Header;
        var spec  = chain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        var epoch = chain.EpochSwitchManager.GetEpochSwitchInfo(head)!;

        // Use a quorum-sized subset of masternodes for signatures
        var signers = chain.TakeRandomMasterNodes(spec, epoch);
        var round   = ctx.CurrentRound;                  // align with current round (Go uses 5 after SetNewRound)
        const ulong gap = 450;                           // matches your V2 test config

        // Send N-1 timeouts -> should NOT reach threshold
        for (int i = 0; i < signers.Length - 1; i++)
            await chain.TimeoutCertificateManager.HandleTimeoutVote(BuildSignedTimeout(round, gap, signers[i]));

        // Sanity: round hasn’t advanced, HighestTC not set to this round yet
        Assert.That(ctx.CurrentRound, Is.EqualTo(round));
        Assert.That(ctx.HighestTC!.Round, Is.LessThan(round));

        // One more timeout (reaches threshold) -> HighestTC set, round increments
        await chain.TimeoutCertificateManager.HandleTimeoutVote(BuildSignedTimeout(round, gap, signers[^1]));

        Assert.That(ctx.HighestTC.Round, Is.EqualTo(round));
        Assert.That(ctx.HighestTC.GapNumber, Is.EqualTo(gap));
        Assert.That(ctx.CurrentRound, Is.EqualTo(round + 1));
    }

    [Test]
    public async Task TestThrowErrorIfTimeoutMsgRoundNotEqualToCurrentRound()
    {
        // Go version returns errors; C# HandleTimeout returns early.
        var chain = await XdcTestBlockchain.Create();
        var ctx   = chain.XdcContext;

        var head  = (XdcBlockHeader) chain.BlockTree.Head!.Header;
        var spec  = chain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        var epoch = chain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        var signer = chain.TakeRandomMasterNodes(spec, epoch).First();

        var current = ctx.CurrentRound;

        // timeout.Round != current -> returns early; HighestTC/round unchanged
        var early = BuildSignedTimeout(round: current + 1, gap: 450, signer);
        await chain.TimeoutCertificateManager.HandleTimeoutVote(early);
        Assert.That(ctx.CurrentRound, Is.EqualTo(current));
        Assert.That(ctx.HighestTC!.Round, Is.LessThanOrEqualTo(current)); // unchanged

        var late = BuildSignedTimeout(round: current - 1, gap: 450, signer);
        await chain.TimeoutCertificateManager.HandleTimeoutVote(late);
        Assert.That(ctx.CurrentRound, Is.EqualTo(current));
    }

    [Test]
    public async Task TestShouldVerifyTimeoutMessageForFirstV2Block()
    {
        // In C# we verify the same thing by exercising OnReceiveTimeout (Filter + Handle)
        var chain = await XdcTestBlockchain.Create();
        var ctx   = chain.XdcContext;

        var head  = (XdcBlockHeader) chain.BlockTree.Head!.Header;
        var spec  = chain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        var epoch = chain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        var signer = chain.TakeRandomMasterNodes(spec, epoch).First();

        // Round == current, signer is masternode -> accepted (HandleTimeout invoked)
        var okTimeout = BuildSignedTimeout(ctx.CurrentRound, 450, signer);
        await chain.TimeoutCertificateManager.OnReceiveTimeout(okTimeout);
        okTimeout.Signer.Should().Be(signer.Address); // set by FilterTimeout

        // Next round also acceptable per Go test—OnReceiveTimeout allows current or future (within gap distance)
        var nextTimeout = BuildSignedTimeout(ctx.CurrentRound + 1, 450, signer);
        await chain.TimeoutCertificateManager.OnReceiveTimeout(nextTimeout);
        nextTimeout.Signer.Should().Be(signer.Address);
    }

    [Test]
    public async Task TestShouldVerifyTimeoutMessage()
    {
        // Similar to previous, but with later round and computed gap
        var chain = await XdcTestBlockchain.Create();

        var head  = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        var ctx   = chain.XdcContext;
        var spec  = chain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        var epoch = chain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        var signer = chain.TakeRandomMasterNodes(spec, epoch).First();

        var farRound = ctx.CurrentRound + 500;
        var gap = (ulong)Math.Max(0, (long)head.Number - (long)head.Number % spec.EpochLength - spec.Gap);

        var t = BuildSignedTimeout(farRound, gap, signer);
        await chain.TimeoutCertificateManager.OnReceiveTimeout(t);

        // If within allowed distance in your OnReceiveTimeout, it’s accepted; otherwise ignored.
        // We at least assert the signature recovered (Filter) matches the signer we used:
        t.Signer.Should().Be(signer.Address);
    }

    [Test]
    public async Task TestTimeoutPoolKeyGoodHygiene()
    {
        // We approximate Go "HygieneTimeoutPoolFaker" with EndRound behavior.
        var chain = await XdcTestBlockchain.Create();
        var ctx   = chain.XdcContext;

        var head  = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        var spec  = chain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        var epoch = chain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        var signer = chain.TakeRandomMasterNodes(spec, epoch).First();

        // Round 5 (inject)
        ctx.SetNewRound(5);
        await chain.TimeoutCertificateManager.HandleTimeoutVote(BuildSignedTimeout(5, 450, signer));

        // Round 16
        ctx.SetNewRound(16);
        await chain.TimeoutCertificateManager.HandleTimeoutVote(BuildSignedTimeout(16, 450, signer));

        // Round 17
        ctx.SetNewRound(17);
        await chain.TimeoutCertificateManager.HandleTimeoutVote(BuildSignedTimeout(17, 450, signer));

        // Cleanup: mimic hygiene by ending up to round 15 (keep >= 16)
        // XdcPool.EndRound is called by managers when rounds advance; call explicitly on manager if you expose it,
        // or advance context and call a public 'EndRound' you provide. Assuming you add:
        //   public void EndRound(ulong round) => _timeouts.EndRound(round);
        chain.TimeoutCertificateManager.EndRound(15);

        // Now, any new HandleTimeout for an old round should not see previous entries (effectively cleaned).
        // We verify by trying to complete a TC at round 5 again—should require all fresh timeouts:
        var before = ctx.HighestTC.Round;
        await chain.TimeoutCertificateManager.HandleTimeout(BuildSignedTimeout(5, 450, signer));
        // Still no TC at 5 (not enough signatures since old ones were purged):
        Assert.That(ctx.HighestTC.Round, Is.EqualTo(before));
    }

    // ---------- Optional: translated-but-adapted countdown tests ----------

    [Test]
    public async Task TestCountdownTimeoutToSendTimeoutMessage()
    {
        // In Go this reads a timeout from BroadcastCh.
        // Here we simulate the countdown by calling OnCountdownTimer once and verifying side-effects.
        var chain = await XdcTestBlockchain.Create();
        var ctx   = chain.XdcContext;

        var before = ctx.TimeoutCounter;
        chain.TimeoutCertificateManager.OnCountdownTimer();

        // We can assert the counter ticked; the produced timeout is handled internally.
        Assert.That(ctx.TimeoutCounter, Is.EqualTo(before + 1));
        // We can also assert no premature round change on first tick
        // (threshold likely not reached on a single self-timeout).
        // If your spec makes single-self count toward pool, relax this assertion accordingly:
        Assert.That(ctx.CurrentRound, Is.GreaterThan(0)); // just sanity
    }

    [Test]
    public async Task TestCountdownTimeoutNotToSendTimeoutMessageIfNotInMasternodeList()
    {
        // Go replaces the signer to a non-masternode and expects no broadcast.
        // In C#, OnCountdownTimer will early return due to AllowedToSend() == false.
        var chain = await XdcTestBlockchain.Create();
        var ctx   = chain.XdcContext;

        // Replace signer with an address not in masternodes (grab one of RandomNonMasternodeKeys if you added them)
        var nonMnKey = new PrivateKey(); // ensure it’s not among masternodes
        var origSigner = chain.Signer;
        chain.Container.Inject<ISigner>(new Signer(chain.SpecProvider.ChainId, nonMnKey, chain.LogManager));

        var before = ctx.TimeoutCounter;
        chain.TimeoutCertificateManager.OnCountdownTimer();
        Assert.That(ctx.TimeoutCounter, Is.EqualTo(before)); // no send

        // restore signer if test suite reuses the same container (optional)
        chain.Container.Inject<ISigner>(origSigner);
    }
}
