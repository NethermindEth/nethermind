// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Codecs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Testing.Platform.Extensions.Messages;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;
using Org.BouncyCastle.Utilities.Encoders;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal class XdcSealValidatorTests
{
    [Test]
    public void ValidateSeal_NotXdcHeader_ThrowArgumentException()
    {
        XdcSealValidator validator = new XdcSealValidator(Substitute.For<ISnapshotManager>(), Substitute.For<ISpecProvider>());
        BlockHeader header = new BlockHeader(
            parentHash: Hash256.Zero,
            unclesHash: Hash256.Zero,
            beneficiary: Address.Zero,
            difficulty: 0,
            number: 0,
            gasLimit: 0,
            timestamp: 0,
            extraData: Array.Empty<byte>());

        Assert.That(() => validator.ValidateSeal(header), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void ValidateSeal_ValidSeal_ReturnsTrue()
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        XdcSealValidator validator = new XdcSealValidator(Substitute.For<ISnapshotManager>(), Substitute.For<ISpecProvider>());
        XdcBlockHeader header = Build.A.XdcBlockHeader()
        .TestObject;
        header.Beneficiary = TestItem.AddressA;
        header.Validator = ecdsa.Sign(TestItem.PrivateKeyA, header).BytesWithRecovery;

        Assert.That(validator.ValidateSeal(header), Is.True);
    }

    public static IEnumerable<TestCaseData> InvalidSignatureCases()
    {
        XdcBlockHeader header =
            Build.A.XdcBlockHeader()
            .TestObject;
        header.Beneficiary = TestItem.AddressA;
        yield return new TestCaseData(header, new byte[0]);
        yield return new TestCaseData(header, new byte[65]);
        yield return new TestCaseData(header, new byte[66]);
        byte[] extralongSignature = new byte[66];
        var keyASig = new EthereumEcdsa(0).Sign(TestItem.PrivateKeyA, header).BytesWithRecovery;
        keyASig.CopyTo(extralongSignature, 0);
        yield return new TestCaseData(header, extralongSignature);
        var keyBSig = new EthereumEcdsa(0).Sign(TestItem.PrivateKeyB, header).BytesWithRecovery;
        yield return new TestCaseData(header, keyBSig);
    }

    [TestCaseSource(nameof(InvalidSignatureCases))]
    public void ValidateSeal_SignatureIsInvalid_ReturnsFalse(XdcBlockHeader header, byte[] validatorSig)
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        XdcSealValidator validator = new XdcSealValidator(Substitute.For<ISnapshotManager>(), Substitute.For<ISpecProvider>());
        header.Validator = validatorSig;

        Assert.That(validator.ValidateSeal(header), Is.False);
    }

    [Test]
    public void ValidateParams_NotXdcHeader_ThrowArgumentException()
    {
        XdcSealValidator validator = new XdcSealValidator(Substitute.For<ISnapshotManager>(), Substitute.For<ISpecProvider>());
        BlockHeader header = Build.A.BlockHeader.TestObject;

        Assert.That(() => validator.ValidateSeal(header), Throws.InstanceOf<ArgumentException>());
    }

    public static IEnumerable<TestCaseData> SealParameterCases()
    {
        yield return new TestCaseData(901, new BlockRoundInfo(Hash256.Zero, 899, 1), 72, Array.Empty<Address>(), false);
        yield return new TestCaseData(901, new BlockRoundInfo(Hash256.Zero, 899, 1), 72, Array.Empty<Address>(), false);
    }

    [TestCaseSource(nameof(SealParameterCases))]
    public void ValidateParams_Test(int currentRound, BlockRoundInfo blockRoundInfo, int numberOfSigners, Address[] penalties, bool expected)
    {
        PrivateKeyGenerator keyBuilder = new PrivateKeyGenerator();
        PrivateKey[] masterSigners = Enumerable.Range(0, 108).Select(i => keyBuilder.Generate()).ToArray();

        XdcBlockHeader parent =
            Build.A.XdcBlockHeader()
            .TestObject;
        ExtraFieldsV2 extraFieldsV2 = new ExtraFieldsV2((ulong)currentRound, CreateQc(new BlockRoundInfo(Hash256.Zero, 1, 1), masterSigners, numberOfSigners, 1));
        XdcBlockHeader header =
                (XdcBlockHeader)Build.A.XdcBlockHeader()
            .WithExtraFieldsV2(extraFieldsV2)
            .WithValidators(masterSigners.Select(k => k.Address.Bytes).SelectMany(b => b).ToArray())
            .WithPenalties(penalties.Select(k => k.Bytes).SelectMany(b => b).ToArray())
            .WithNumber(1)
            .TestObject;
        header.Author = TestItem.AddressA;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.EpochLength.Returns(900);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager
            .CalculateNextEpochMasternodes(Arg.Any<XdcBlockHeader>())
            .Returns(masterSigners.Select(k=>k.Address).ToImmutableSortedSet());
        snapshotManager
            .GetPenalties(Arg.Any<XdcBlockHeader>())
            .Returns([]);
        XdcSealValidator validator = new XdcSealValidator(snapshotManager, specProvider);

        Assert.That(validator.ValidateParams(parent, header), Is.EqualTo(expected));
    }

    private static QuorumCert CreateQc(BlockRoundInfo roundInfo, PrivateKey[] keys, int numberOfSigners, ulong gapNumber)
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        QuorumCert quorumForSigning = new QuorumCert(roundInfo, null, gapNumber);
        QuorumCertificateDecoder qcEncoder = new QuorumCertificateDecoder();

        IEnumerable<Signature> signatures = Enumerable.Range(0, numberOfSigners).Select(i => ecdsa.Sign(keys[i], Keccak.Compute(qcEncoder.Encode(quorumForSigning, RlpBehaviors.ForSealing).Bytes))) ;

        return new QuorumCert(roundInfo, signatures.ToArray(), gapNumber);
    }
}
