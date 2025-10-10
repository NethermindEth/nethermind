// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.Test;

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
        snapshotManager.GetSnapshot(Arg.Any<Hash256>())
                    .Returns((Snapshot?)null);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);
        var tcManager = new TimeoutCertificateManager(
            snapshotManager,
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<ISpecProvider>(),
            blockTree);

        var ok = tcManager.VerifyTimeoutCertificate(tc, out var err);
        Assert.That(ok, Is.False);
        Assert.That(err, Does.Contain("Failed to get snapshot"));
    }

    [Test]
    public void VerifyTC_EmptyCandidates_ReturnsFalse()
    {
        var tc = new TimeoutCertificate(1, Array.Empty<Signature>(), 0);
        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshot(Arg.Any<Hash256>())
            .Returns(new Snapshot(0, Hash256.Zero, Array.Empty<Address>()));
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;
        blockTree.FindHeader(Arg.Any<long>()).Returns(header);
        var tcManager = new TimeoutCertificateManager(
            snapshotManager,
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<ISpecProvider>(),
            blockTree);

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
        snapshotManager.GetSnapshot(Arg.Any<Hash256>())
            .Returns(new Snapshot(0, Hash256.Zero, masternodes));

        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        var epochSwitchInfo = new EpochSwitchInfo(masternodes, [], new BlockRoundInfo(Hash256.Zero, 1, 10));
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>(), Arg.Any<Hash256>())
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

        var tcManager = new TimeoutCertificateManager(snapshotManager, epochSwitchManager, specProvider, blockTree);

        Assert.That(tcManager.VerifyTimeoutCertificate(timeoutCertificate, out _), Is.EqualTo(expected));
    }

    private TimeoutCertificateManager BuildTimeoutCertificateManager()
    {
        return new TimeoutCertificateManager(
            Substitute.For<ISnapshotManager>(),
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockTree>());
    }

    private static TimeoutCertificate BuildTimeoutCertificate(PrivateKey[] keys, ulong round = 1, ulong gap = 0)
    {
        var ecdsa = new EthereumEcdsa(0);
        ValueHash256 msgHash = TimeoutCertificateManager.ComputeTimeoutMsgHash(round, gap);
        Signature[] signatures = keys.Select(k => ecdsa.Sign(k, msgHash)).ToArray();
        return new TimeoutCertificate(round, signatures, gap);
    }
}
