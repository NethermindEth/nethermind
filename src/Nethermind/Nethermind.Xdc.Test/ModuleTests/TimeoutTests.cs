// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

public class TimeoutTests
{
    [Test]
    public async Task TestCountdownTimeoutToSendTimeoutMessage()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create();
        ITimeoutCertificateManager tcManager = blockchain.TimeoutCertificateManager;
        IXdcConsensusContext ctx = blockchain.XdcContext;
        tcManager.OnCountdownTimer();

        Timeout expectedTimeoutMsg = XdcTestHelper.BuildSignedTimeout(blockchain.Signer.Key!, ctx.CurrentRound, 0);

        Assert.That(ctx.TimeoutCounter, Is.EqualTo(1));
        Assert.That(tcManager.GetTimeoutsCount(expectedTimeoutMsg), Is.EqualTo(1));
    }

    [Test]
    public async Task TestCountdownTimeoutNotToSendTimeoutMessageIfNotInMasternodeList()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create();
        // Create TCManager with a signer not in the Masternode list
        blockchain.Signer.SetSigner(TestItem.PrivateKeyA);

        blockchain.TimeoutCertificateManager.OnCountdownTimer();

        // Since the signer is not in masternode list, method should return early
        Assert.That(blockchain.XdcContext.TimeoutCounter, Is.EqualTo(0));
    }

    [Test]
    public async Task TestTimeoutMessageHandlerSuccessfullyGenerateTC()
    {
        using XdcTestBlockchain blockchain = await XdcTestBlockchain.Create();

        IXdcConsensusContext ctx = blockchain.XdcContext;
        XdcBlockHeader head = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = blockchain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        EpochSwitchInfo epoch = blockchain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        PrivateKey[] signers = blockchain.TakeRandomMasterNodes(spec, epoch);
        ulong round = ctx.CurrentRound;
        const ulong gap = 450;
        List<Signature> signatures = new();

        // Send N-1 timeouts -> should NOT reach threshold
        for (int i = 0; i < signers.Length - 1; i++)
        {
            Timeout timeoutMsg = XdcTestHelper.BuildSignedTimeout(signers[i], round, gap);
            await blockchain.TimeoutCertificateManager.HandleTimeoutVote(timeoutMsg);
            signatures.Add(timeoutMsg.Signature!);
        }

        // Sanity check: round hasn’t advanced, HighestTC not set to this round yet
        Assert.That(ctx.CurrentRound, Is.EqualTo(round));
        Assert.That(ctx.HighestTC.Round, Is.EqualTo(0));

        // Send timeout message with wrong gap so it doesn't reach threshold yet
        await blockchain.TimeoutCertificateManager.HandleTimeoutVote(XdcTestHelper.BuildSignedTimeout(signers.Last(), round, 1350));
        Assert.That(ctx.CurrentRound, Is.EqualTo(round));
        Assert.That(ctx.HighestTC.Round, Is.EqualTo(0));

        // One more timeout (reaches threshold) -> HighestTC set, round increments
        Timeout lastTimeoutMsg = XdcTestHelper.BuildSignedTimeout(signers.Last(), round, gap);
        await blockchain.TimeoutCertificateManager.HandleTimeoutVote(lastTimeoutMsg);
        signatures.Add(lastTimeoutMsg.Signature!);

        TimeoutCertificate expectedTC = new(round, signatures.ToArray(), gap);
        ctx.HighestTC.Should().BeEquivalentTo(expectedTC);
        Assert.That(ctx.CurrentRound, Is.EqualTo(round + 1));
    }
}
