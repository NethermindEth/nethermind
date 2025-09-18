// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
public class QuorumCertificateManagerTest
{
    [Test]
    public void VerifyCertificate_CertificateIsNull_ThrowsArgumentNullException()
    {
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IXdcReleaseSpec>(),
            Substitute.For<IEpochSwitchManager>());

        Assert.That(() => quorumCertificateManager.VerifyCertificate(null!, Build.A.XdcBlockHeader().TestObject, out _), Throws.ArgumentNullException);
    }

    [Test]
    public void VerifyCertificate_HeaderIsNull_ThrowsArgumentNullException()
    {
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IXdcReleaseSpec>(),
            Substitute.For<IEpochSwitchManager>());

        Assert.That(() => quorumCertificateManager.VerifyCertificate(Build.A.QuorumCertificate().TestObject, null!, out _), Throws.ArgumentNullException);
    }

    public static IEnumerable<TestCaseData> QcCases()
    {
        PrivateKeyBuilder keyBuilder = Build.A.PrivateKey;
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader();

        //Base valid control case
        yield return new TestCaseData(CreateQc(new BlockInfo(Hash256.Zero, 1, 1), 1, keyBuilder.TestObjectNTimes(20)), headerBuilder, true);
    }

    [TestCaseSource(nameof(QcCases))]
    public void VerifyCertificate_(QuorumCertificate quorumCert, XdcBlockHeaderBuilder xdcBlockHeaderBuilder, bool expected)
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager.TryGetEpochSwitchInfo(Arg.Any<XdcBlockHeader>(), Arg.Any<Hash256>(), out EpochSwitchInfo epochSwitch).Returns(true);
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IXdcReleaseSpec>(),
            epochSwitchManager);

        Assert.That(quorumCertificateManager.VerifyCertificate(quorumCert, xdcBlockHeaderBuilder.TestObject, out _), Is.EqualTo(expected));
    }

    private static QuorumCertificate CreateQc(BlockInfo roundInfo, ulong gapNumber, PrivateKey[] keys)
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        var qcEncoder = new VoteDecoder();

        IEnumerable<Signature> signatures = CreateVoteSignatures(roundInfo, gapNumber, keys);

        return new QuorumCertificate(roundInfo, signatures.ToArray(), gapNumber);
    }

    private static Signature[] CreateVoteSignatures(BlockInfo roundInfo, ulong gapnumber, PrivateKey[] keys)
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        var encoder = new VoteDecoder();
        IEnumerable<Signature> signatures = keys.Select(k =>
        {
            var stream = new KeccakRlpStream();
            encoder.Encode(stream, new Vote(roundInfo, gapnumber), RlpBehaviors.ForSealing);
            return ecdsa.Sign(k, stream.GetValueHash());
        }).ToArray();
        return signatures.ToArray();
    }
}
