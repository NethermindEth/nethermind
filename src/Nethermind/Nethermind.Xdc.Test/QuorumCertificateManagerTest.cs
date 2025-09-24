// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
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
            Substitute.For<IDb>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IEpochSwitchManager>());

        Assert.That(() => quorumCertificateManager.VerifyCertificate(null!, Build.A.XdcBlockHeader().TestObject, out _), Throws.ArgumentNullException);
    }

    [Test]
    public void VerifyCertificate_HeaderIsNull_ThrowsArgumentNullException()
    {
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IDb>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IEpochSwitchManager>());

        Assert.That(() => quorumCertificateManager.VerifyCertificate(Build.A.QuorumCertificate().TestObject, null!, out _), Throws.ArgumentNullException);
    }

    public static IEnumerable<TestCaseData> QcCases()
    {
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData();
        var keyBuilder = new PrivateKeyGenerator();
        //Base valid control case
        PrivateKey[] keys = keyBuilder.Generate(20).ToArray();
        IEnumerable<Address> masterNodes = keys.Select(k => k.Address);
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 0, keys), headerBuilder, keys.Select(k => k.Address), true);

        //Not enough signatures
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 0, keys.Take(13).ToArray()), headerBuilder, keys.Select(k => k.Address), false);

        //1 Vote is not master node
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 0, keys), headerBuilder, keys.Skip(1).Select(k => k.Address), false);

        //Wrong gap number
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 1, keys), headerBuilder, masterNodes, false);

        //Wrong block number in QC
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 2), 0, keys), headerBuilder, masterNodes, false);

        //Wrong hash in QC
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(Hash256.Zero, 1, 1), 0, keys), headerBuilder, masterNodes, false);

        //Wrong round number in QC
        yield return new TestCaseData(CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 0, 1), 0, keys), headerBuilder, masterNodes, false);
    }

    [TestCaseSource(nameof(QcCases))]
    public void VerifyCertificate_(QuorumCertificate quorumCert, XdcBlockHeaderBuilder xdcBlockHeaderBuilder, IEnumerable<Address> masternodes, bool expected)
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>(), Arg.Any<Hash256>())
            .Returns(new EpochSwitchInfo(masternodes.ToArray(), [], new BlockRoundInfo(Hash256.Zero, 1, 10)));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.EpochLength.Returns(900);
        xdcReleaseSpec.Gap.Returns(450);
        IXdcSubConfig xdcSubConfig = Substitute.For<IXdcSubConfig>();
        xdcSubConfig.CertThreshold.Returns(0.667);
        xdcReleaseSpec.Configs.Returns([xdcSubConfig, xdcSubConfig]);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IDb>(),
            specProvider,
            epochSwitchManager);

        Assert.That(quorumCertificateManager.VerifyCertificate(quorumCert, xdcBlockHeaderBuilder.TestObject, out _), Is.EqualTo(expected));
    }

    private static QuorumCertificate CreateQc(BlockRoundInfo roundInfo, ulong gapNumber, PrivateKey[] keys)
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        var qcEncoder = new VoteDecoder();

        IEnumerable<Signature> signatures = CreateVoteSignatures(roundInfo, gapNumber, keys);

        return new QuorumCertificate(roundInfo, signatures.ToArray(), gapNumber);
    }

    private static Signature[] CreateVoteSignatures(BlockRoundInfo roundInfo, ulong gapnumber, PrivateKey[] keys)
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
