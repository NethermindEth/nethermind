// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Xdc.Types;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Xdc.Test.ModuleTests;

public class TimeoutTests
{
    [Test]
    public async Task TestCountdownTimeoutToSendTimeoutMessage()
    {
        var blockchain = await XdcTestBlockchain.Create();
        var tcManager = blockchain.TimeoutCertificateManager;
        var ctx = blockchain.XdcContext;
        tcManager.OnCountdownTimer();

        Timeout expectedTimeoutMsg = XdcTestHelper.BuildSignedTimeout(blockchain.Signer.Key!, ctx.CurrentRound, 0);

        Assert.That(ctx.TimeoutCounter, Is.EqualTo(1));
        Assert.That(tcManager.GetTimeoutsCount(expectedTimeoutMsg), Is.EqualTo(1));
    }

    [Test]
    public async Task TestCountdownTimeoutNotToSendTimeoutMessageIfNotInMasternodeList()
    {
        var blockchain = await XdcTestBlockchain.Create();
        // Create TCManager with a signer not in the Masternode list
        var extraKey = blockchain.RandomKeys.First();

        blockchain.Signer.SetSigner(TestItem.PrivateKeyA);

        blockchain.TimeoutCertificateManager.OnCountdownTimer();

        // Since the signer is not in masternode list, method should return early
        Assert.That(blockchain.XdcContext.TimeoutCounter, Is.EqualTo(0));
    }

    [Test]
    public async Task TestTimeoutMessageHandlerSuccessfullyGenerateTC()
    {
        var blockchain = await XdcTestBlockchain.Create();

        var ctx = blockchain.XdcContext;
        var head = (XdcBlockHeader)blockchain.BlockTree.Head!.Header;
        var spec = blockchain.SpecProvider.GetXdcSpec(head, ctx.CurrentRound);
        var epoch = blockchain.EpochSwitchManager.GetEpochSwitchInfo(head)!;
        var signers = blockchain.TakeRandomMasterNodes(spec, epoch);
        var round = ctx.CurrentRound;
        const ulong gap = 450;
        var signatures = new List<Signature>();

        // Send N-1 timeouts -> should NOT reach threshold
        for (int i = 0; i < signers.Length - 1; i++)
        {
            Timeout timeoutMsg = XdcTestHelper.BuildSignedTimeout(signers[i], round, gap);
            await blockchain.TimeoutCertificateManager.HandleTimeoutVote(timeoutMsg);
            signatures.Add(timeoutMsg.Signature!);
        }

        // Sanity check: round hasn’t advanced, HighestTC not set to this round yet
        Assert.That(ctx.CurrentRound, Is.EqualTo(round));
        Assert.That(ctx.HighestTC, Is.Null);

        // Send timeout message with wrong gap so it doesn't reach threshold yet
        await blockchain.TimeoutCertificateManager.HandleTimeoutVote(XdcTestHelper.BuildSignedTimeout(signers.Last(), round, 1350));
        Assert.That(ctx.CurrentRound, Is.EqualTo(round));
        Assert.That(ctx.HighestTC, Is.Null);

        // One more timeout (reaches threshold) -> HighestTC set, round increments
        Timeout lastTimeoutMsg = XdcTestHelper.BuildSignedTimeout(signers.Last(), round, gap);
        await blockchain.TimeoutCertificateManager.HandleTimeoutVote(lastTimeoutMsg);
        signatures.Add(lastTimeoutMsg.Signature!);

        var expectedTC = new TimeoutCertificate(round, signatures.ToArray(), gap);
        ctx.HighestTC.Should().BeEquivalentTo(expectedTC);
        Assert.That(ctx.CurrentRound, Is.EqualTo(round + 1));
    }
}
