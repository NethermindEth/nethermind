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

        (XdcBlockHeaderBuilder headerBuilder, PrivateKey[] masterSigners) = CreateValidEpochSwitchHeader();
        //Base valid control case
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), true);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        headerBuilder.WithExtraData(Array.Empty<byte>());
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Current round is same as QC round
        headerBuilder.WithExtraFieldsV2(new ExtraFieldsV2(1, CreateQc(new BlockRoundInfo(Hash256.Zero, 1, 1), masterSigners, 1)));
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Invalid nonce for epoch switch
        headerBuilder.WithNonce(1);
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Remove one from validator list
        headerBuilder.WithValidators(masterSigners.Select(m=>m.Address).Take(masterSigners.Length - 1).ToArray());
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Remove one from epoch candidates
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address).Take(masterSigners.Length - 1), Array.Empty<Address>(), false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Header penalties not matching epoch snapshot
        headerBuilder.WithPenalties(new[] {Address.Zero});
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Header penalties not matching epoch snapshot
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), new[] {Address.Zero}, false);

        (headerBuilder, masterSigners) = CreateValidEpochSwitchHeader();
        //Block sealer is not the leader in this round
        headerBuilder.WithAuthor(masterSigners[1].Address);
        yield return new TestCaseData(headerBuilder, masterSigners.Select(m => m.Address), Array.Empty<Address>(), false);

        (XdcBlockHeaderBuilder headerBuilder, PrivateKey[] masterSigners) CreateValidEpochSwitchHeader()
        {
            XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader();

            PrivateKeyGenerator keyBuilder = new PrivateKeyGenerator();
            PrivateKey[] masterSigners = Enumerable.Range(0, 108).Select(i => keyBuilder.Generate()).ToArray();
            PrivateKey[] qcSigners = masterSigners.Take(72).ToArray();

            var extraFieldsV2 = new ExtraFieldsV2(1800, CreateQc(new BlockRoundInfo(Hash256.Zero, 1, 1), masterSigners, 1));
            headerBuilder.WithExtraFieldsV2(extraFieldsV2);
            headerBuilder.WithValidators(masterSigners.Select(m => m.Address).ToArray());
            headerBuilder.WithAuthor(masterSigners[0].Address);
            return (headerBuilder, masterSigners);
        }
    }

    [TestCaseSource(nameof(SealParameterCases))]
    public void ValidateParams_HeaderIsEpochSwitch_ReturnsExpected(XdcBlockHeaderBuilder headerBuilder, IEnumerable<Address> epochCandidates, IEnumerable<Address> penalties, bool expected)
    {
        XdcBlockHeader parent =
            Build.A.XdcBlockHeader()
            .TestObject;
        headerBuilder.WithParent(parent); 

        XdcBlockHeader header = headerBuilder.TestObject;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec releaseSpec = Substitute.For<IXdcReleaseSpec>();
        releaseSpec.EpochLength.Returns(900);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager
            .CalculateNextEpochMasternodes(Arg.Any<XdcBlockHeader>())
            .Returns(epochCandidates.ToArray());
        snapshotManager
            .GetPenalties(Arg.Any<XdcBlockHeader>())
            .Returns(penalties.ToArray());
        XdcSealValidator validator = new XdcSealValidator(snapshotManager, specProvider);

        Assert.That(validator.ValidateParams(parent, header), Is.EqualTo(expected));
    }

    private static QuorumCert CreateQc(BlockRoundInfo roundInfo, PrivateKey[] keys, ulong gapNumber)
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        QuorumCert quorumForSigning = new QuorumCert(roundInfo, null, gapNumber);
        QuorumCertificateDecoder qcEncoder = new QuorumCertificateDecoder();

        IEnumerable<Signature> signatures = keys.Select(k => ecdsa.Sign(k, Keccak.Compute(qcEncoder.Encode(quorumForSigning, RlpBehaviors.ForSealing).Bytes)));

        return new QuorumCert(roundInfo, signatures.ToArray(), gapNumber);
    }
}
