// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
public class TimeoutCertificateManagerTests
{

    [Test]
    public void VerifyTC_NullCert_Throws()
    {
        TimeoutCertificateManager tcManager = BuildTimeoutCertificateManager();
        Assert.That(() => tcManager.VerifyTimeoutCertificate(null!, out _), Throws.ArgumentNullException);
    }

    [Test]
    public void VerifyTC_NullSignatures_Throws()
    {
        TimeoutCertificateManager tcManager = BuildTimeoutCertificateManager();
        var tc = new TimeoutCertificate(1, null!, 0);
        Assert.That(() => tcManager.VerifyTimeoutCertificate(tc, out _), Throws.ArgumentNullException);
    }

    [Test]
    public void VerifyTC_SnapshotMissing_ReturnsFalse()
    {
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(Arg.Any<long>())
                    .Returns((Snapshot?)null);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree.Head.Returns(new Block(header));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new XdcReleaseSpec() { V2Configs = [new V2ConfigParams()] });
        var tcManager = new TimeoutCertificateManager(
            new XdcConsensusContext(),
            snapshotManager,
            Substitute.For<IEpochSwitchManager>(),
            specProvider,
            blockTree,
            Substitute.For<ISyncInfoManager>(),
            Substitute.For<ISigner>());

        var ok = tcManager.VerifyTimeoutCertificate(tc, out var err);
        Assert.That(ok, Is.False);
        Assert.That(err, Does.Contain("Failed to get snapshot"));
    }

    [Test]
    public void VerifyTC_EmptyCandidates_ReturnsFalse()
    {
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(Arg.Any<long>())
            .Returns(new Snapshot(0, Hash256.Zero, Array.Empty<Address>()));
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree
            .Head
            .Returns(new Block(header));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new XdcReleaseSpec() { V2Configs = [new V2ConfigParams()] });
        var tcManager = new TimeoutCertificateManager(
            new XdcConsensusContext(),
            snapshotManager,
            Substitute.For<IEpochSwitchManager>(),
            specProvider,
            blockTree,
            Substitute.For<ISyncInfoManager>(),
            Substitute.For<ISigner>());

        var ok = tcManager.VerifyTimeoutCertificate(tc, out var err);
        Assert.That(ok, Is.False);
        Assert.That(err, Does.Contain("Empty master node"));
    }

    public static IEnumerable<TestCaseData> TcCases()
    {
        var keyBuilder = new PrivateKeyGenerator();
        PrivateKey[] keys = keyBuilder.Generate(20).ToArray();
        IEnumerable<Address> masterNodes = keys.Select(k => k.Address);

        // Base case
        yield return new TestCaseData(BuildTimeoutCertificate(keys), masterNodes, true);

        // Insufficient signature count
        PrivateKey[] notEnoughKeys = keys.Take(13).ToArray();
        yield return new TestCaseData(BuildTimeoutCertificate(notEnoughKeys), masterNodes, false);

        // Duplicated signatures still should fail if not enough
        yield return new TestCaseData(BuildTimeoutCertificate(notEnoughKeys.Concat(notEnoughKeys).ToArray()), masterNodes, false);

        // Sufficient signature count
        yield return new TestCaseData(BuildTimeoutCertificate(keys.Take(14).ToArray()), masterNodes, true);

        // Signer not in master nodes
        yield return new TestCaseData(BuildTimeoutCertificate(keys), keys.Skip(1).Select(k => k.Address), false);
    }

    [TestCaseSource(nameof(TcCases))]
    public void VerifyTCWithDifferentParameters_ReturnsExpected(TimeoutCertificate timeoutCertificate, IEnumerable<Address> masternodesList, bool expected)
    {
        Address[] masternodes = masternodesList.ToArray();
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(Arg.Any<long>())
            .Returns(new Snapshot(0, Hash256.Zero, masternodes));

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        var epochSwitchInfo = new EpochSwitchInfo(masternodes, [], [], new BlockRoundInfo(Hash256.Zero, 1, 10));
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>())
            .Returns(epochSwitchInfo);
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<Hash256>())
            .Returns(epochSwitchInfo);
        epochSwitchManager.GetTimeoutCertificateEpochInfo(Arg.Any<TimeoutCertificate>()).Returns(epochSwitchInfo);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.EpochLength.Returns(900);
        xdcReleaseSpec.SwitchEpoch.Returns(89300);
        xdcReleaseSpec.CertThreshold.Returns(0.667);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree.Head.Returns(new Block(header, new BlockBody()));
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);

        var context = new XdcConsensusContext();
        ISyncInfoManager syncInfoManager = Substitute.For<ISyncInfoManager>();
        ISigner signer = Substitute.For<ISigner>();

        var tcManager = new TimeoutCertificateManager(context, snapshotManager, epochSwitchManager, specProvider,
            blockTree, syncInfoManager, signer);

        Assert.That(tcManager.VerifyTimeoutCertificate(timeoutCertificate, out _), Is.EqualTo(expected));
    }

    [TestCase(4UL)]
    [TestCase(6UL)]
    public async Task HandleTimeoutVote_RoundDoesNotMatchCurrentRound_ShouldReturnEarly(ulong round)
    {
        var ctx = new XdcConsensusContext() { CurrentRound = 5 };
        var tcManager = BuildTimeoutCertificateManager(ctx);
        // dummy timeout message, only care about the round
        var timeout = new Timeout(round, null, 0);
        await tcManager.HandleTimeoutVote(timeout);
        Assert.That(tcManager.GetTimeoutsCount(timeout), Is.EqualTo(0));
    }

    [TestCase(99UL, 0UL, true, false)]     // Round smaller than current round
    [TestCase(100UL, 1000UL, true, false)] // Incorrect gap number, snapshot is null
    [TestCase(100UL, 0UL, false, false)]   // Signer not in masternodes candidates
    [TestCase(500UL, 0UL, true, true)]     // Far away round but should get filtered in
    public void FilterTimeout_DifferentCases_ReturnsExpected(ulong round, ulong gap, bool correctSigner, bool expected)
    {
        var keyBuilder = new PrivateKeyGenerator();
        PrivateKey[] keys = keyBuilder.Generate(21).ToArray();
        var masternodes = keys.Take(20).Select(k => k.Address).ToArray();
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByGapNumber(0)
            .Returns(new Snapshot(0, Hash256.Zero, masternodes));

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree.Head.Returns(new Block(header, new BlockBody()));

        var context = new XdcConsensusContext() { CurrentRound = 100 };
        ISyncInfoManager syncInfoManager = Substitute.For<ISyncInfoManager>();
        ISigner signer = Substitute.For<ISigner>();

        var tcManager = new TimeoutCertificateManager(context, snapshotManager, epochSwitchManager, specProvider,
            blockTree, syncInfoManager, signer);

        var key = correctSigner ? keys.First() : keys.Last();
        var timeout = XdcTestHelper.BuildSignedTimeout(key, round, gap);
        Assert.That(tcManager.FilterTimeout(timeout), Is.EqualTo(expected));
    }

    private TimeoutCertificateManager BuildTimeoutCertificateManager(XdcConsensusContext? ctx = null)
    {
        return new TimeoutCertificateManager(
            ctx ?? new XdcConsensusContext(),
            Substitute.For<ISnapshotManager>(),
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISyncInfoManager>(),
            Substitute.For<ISigner>());
    }

    private static TimeoutCertificate BuildTimeoutCertificate(PrivateKey[] keys, ulong round = 1, ulong gap = 0)
    {
        var ecdsa = new EthereumEcdsa(0);
        ValueHash256 msgHash = TimeoutCertificateManager.ComputeTimeoutMsgHash(round, gap);
        Signature[] signatures = keys.Select(k => ecdsa.Sign(k, msgHash)).ToArray();
        return new TimeoutCertificate(round, signatures, gap);
    }
}
