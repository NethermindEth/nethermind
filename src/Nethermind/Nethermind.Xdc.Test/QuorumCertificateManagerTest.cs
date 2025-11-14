// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
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
            new XdcConsensusContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<ILogManager>());

        Assert.That(() => quorumCertificateManager.VerifyCertificate(null!, Build.A.XdcBlockHeader().TestObject, out _), Throws.ArgumentNullException);
    }

    [Test]
    public void VerifyCertificate_HeaderIsNull_ThrowsArgumentNullException()
    {
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcConsensusContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IEpochSwitchManager>(),
            Substitute.For<ILogManager>());

        Assert.That(() => quorumCertificateManager.VerifyCertificate(Build.A.QuorumCertificate().TestObject, null!, out _), Throws.ArgumentNullException);
    }

    public static IEnumerable<TestCaseData> QcCases()
    {
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData();
        var keyBuilder = new PrivateKeyGenerator();
        //Base valid control case
        PrivateKey[] keys = keyBuilder.Generate(20).ToArray();
        IEnumerable<Address> masterNodes = keys.Select(k => k.Address);
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 0, keys), headerBuilder, keys.Select(k => k.Address), true);

        //Not enough signatures
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 0, keys.Take(13).ToArray()), headerBuilder, keys.Select(k => k.Address), false);

        //1 Vote is not master node
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 0, keys), headerBuilder, keys.Skip(1).Select(k => k.Address), false);

        //Wrong gap number
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 1), 1, keys), headerBuilder, masterNodes, false);

        //Wrong block number in QC
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 1, 2), 0, keys), headerBuilder, masterNodes, false);

        //Wrong hash in QC
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(Hash256.Zero, 1, 1), 0, keys), headerBuilder, masterNodes, false);

        //Wrong round number in QC
        yield return new TestCaseData(XdcTestHelper.CreateQc(new BlockRoundInfo(headerBuilder.TestObject.Hash!, 0, 1), 0, keys), headerBuilder, masterNodes, false);
    }

    [TestCaseSource(nameof(QcCases))]
    public void VerifyCertificate_QcWithDifferentParameters_ReturnsExpected(QuorumCertificate quorumCert, XdcBlockHeaderBuilder xdcBlockHeaderBuilder, IEnumerable<Address> masternodes, bool expected)
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<XdcBlockHeader>())
            .Returns(new EpochSwitchInfo(masternodes.ToArray(), [], [], new BlockRoundInfo(Hash256.Zero, 1, 10)));
        epochSwitchManager
            .GetEpochSwitchInfo(Arg.Any<Hash256>())
            .Returns(new EpochSwitchInfo(masternodes.ToArray(), [], [], new BlockRoundInfo(Hash256.Zero, 1, 10)));
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        xdcReleaseSpec.EpochLength.Returns(900);
        xdcReleaseSpec.Gap.Returns(450);
        xdcReleaseSpec.CertThreshold.Returns(0.667);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        var quorumCertificateManager = new QuorumCertificateManager(
            new XdcConsensusContext(),
            Substitute.For<IBlockTree>(),
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());

        Assert.That(quorumCertificateManager.VerifyCertificate(quorumCert, xdcBlockHeaderBuilder.TestObject, out _), Is.EqualTo(expected));
    }

    [Test]
    public void CommitCertificate_HeaderDoesNotExists_ThrowInvalidOperationException()
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        XdcConsensusContext context = new XdcConsensusContext();
        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            Substitute.For<IBlockTree>(),
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 1, 0)).TestObject;

        Assert.That(() => quorumCertificateManager.CommitCertificate(qc), Throws.TypeOf<InvalidBlockException>());
    }

    [Test]
    public void CommitCertificate_QcHasHigherRound_HighestQCIsSet()
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        XdcConsensusContext context = new XdcConsensusContext();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader targetHeader = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData().TestObject;
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(targetHeader);
        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            blockTree,
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());
        context.HighestQC = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 0, 1)).TestObject;
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(targetHeader.Hash!, 1, 0)).TestObject;
        quorumCertificateManager.CommitCertificate(qc);

        Assert.That(context.HighestQC, Is.EqualTo(qc));
    }

    [Test]
    public void CommitCertificate_TargetHeaderDoesNotHaveQc_ThrowBlockchainException()
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        XdcConsensusContext context = new XdcConsensusContext();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader targetHeader = Build.A.XdcBlockHeader().TestObject;
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(targetHeader);
        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            blockTree,
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(targetHeader.Hash!, 1, 0)).TestObject;

        Assert.That(() => quorumCertificateManager.CommitCertificate(qc), Throws.TypeOf<BlockchainException>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CommitCertificate_ParentQcHasHigherRound_LockQCIsSetToParent(bool lockQcIsNull)
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        XdcConsensusContext context = new XdcConsensusContext();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader targetHeader = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData().TestObject;
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(targetHeader);
        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            blockTree,
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());

        if (lockQcIsNull)
            context.LockQC = null;
        else
            context.LockQC = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 0, 1)).TestObject;
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(targetHeader.Hash!, 2, 0)).TestObject;
        quorumCertificateManager.CommitCertificate(qc);

        Assert.That(context.LockQC, Is.EqualTo(targetHeader.ExtraConsensusData!.QuorumCert));
    }

    [Test]
    public void CommitCertificate_QcHasHigherRoundThanCurrent_CurrentRoundIsAdvancedByQcRoundPlusOne()
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);
        XdcConsensusContext context = new XdcConsensusContext();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        XdcBlockHeader targetHeader = Build.A.XdcBlockHeader().WithGeneratedExtraConsensusData().TestObject;
        blockTree.FindHeader(Arg.Any<Hash256>()).Returns(targetHeader);
        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            blockTree,
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());
        context.HighestQC = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 0, 1)).TestObject;
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(targetHeader.Hash!, 1, 0)).TestObject;
        quorumCertificateManager.CommitCertificate(qc);

        Assert.That(context.CurrentRound, Is.EqualTo(qc.ProposedBlockInfo.Round + 1));
    }


    [Test]
    public void CommitCertificate_RoundsAreContinuous_GrandParentHeaderIsFinalized()
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        XdcBlockHeader grandParentHeader = Build.A.XdcBlockHeader().WithExtraFieldsV2(
            new ExtraFieldsV2(1, Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 0, 3)).TestObject)
            ).TestObject;
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .WithParentHash(grandParentHeader.Hash!)
            .WithExtraFieldsV2(
            new ExtraFieldsV2(2, Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(grandParentHeader.Hash!, 1, 4)).TestObject)
            ).TestObject;
        XdcBlockHeader targetHeader = Build.A.XdcBlockHeader()
            .WithParentHash(parentHeader.Hash!)
            .WithExtraFieldsV2(
            new ExtraFieldsV2(3, Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(parentHeader.Hash!, 2, 5)).TestObject)
            ).WithNumber(3).TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(grandParentHeader.Hash!).Returns(grandParentHeader);
        blockTree.FindHeader(parentHeader.Hash!).Returns(parentHeader);
        blockTree.FindHeader(targetHeader.Hash!).Returns(targetHeader);

        XdcConsensusContext context = new XdcConsensusContext();
        context.HighestCommitBlock = new BlockRoundInfo(Hash256.Zero, 0, 1);

        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            blockTree,
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(targetHeader.Hash!, 3, 6)).TestObject;
        quorumCertificateManager.CommitCertificate(qc);

        Assert.That(context.HighestCommitBlock.Hash, Is.EqualTo(grandParentHeader.Hash));
    }

    [Test]
    public void CommitCertificate_RoundsAreNotContinuous_GrandParentHeaderIsNotFinalized()
    {
        IEpochSwitchManager epochSwitchManager = Substitute.For<IEpochSwitchManager>();
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec xdcReleaseSpec = Substitute.For<IXdcReleaseSpec>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcReleaseSpec);

        XdcBlockHeader grandParentHeader = Build.A.XdcBlockHeader().WithExtraFieldsV2(
            new ExtraFieldsV2(1, Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 0, 3)).TestObject)
            ).TestObject;
        XdcBlockHeader parentHeader = Build.A.XdcBlockHeader()
            .WithParentHash(grandParentHeader.Hash!)
            .WithExtraFieldsV2(
            new ExtraFieldsV2(3, Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(grandParentHeader.Hash!, 1, 4)).TestObject)
            ).TestObject;
        XdcBlockHeader targetHeader = Build.A.XdcBlockHeader()
            .WithParentHash(parentHeader.Hash!)
            .WithExtraFieldsV2(
            new ExtraFieldsV2(4, Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(parentHeader.Hash!, 3, 5)).TestObject)
            ).WithNumber(3).TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(grandParentHeader.Hash!).Returns(grandParentHeader);
        blockTree.FindHeader(parentHeader.Hash!).Returns(parentHeader);
        blockTree.FindHeader(targetHeader.Hash!).Returns(targetHeader);

        XdcConsensusContext context = new XdcConsensusContext();
        var startFinalizedBlock = new BlockRoundInfo(Hash256.Zero, 0, 1);
        context.HighestCommitBlock = startFinalizedBlock;

        var quorumCertificateManager = new QuorumCertificateManager(
            context,
            blockTree,
            specProvider,
            epochSwitchManager,
            Substitute.For<ILogManager>());
        QuorumCertificate qc = Build.A.QuorumCertificate().WithBlockInfo(new BlockRoundInfo(targetHeader.Hash!, 4, 6)).TestObject;
        quorumCertificateManager.CommitCertificate(qc);

        Assert.That(context.HighestCommitBlock, Is.EqualTo(startFinalizedBlock));
    }
}
