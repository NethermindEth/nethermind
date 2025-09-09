// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Codecs;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
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
        var keyBSig = new EthereumEcdsa(0).Sign(TestItem.PrivateKeyA, header).BytesWithRecovery;
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

    [TestCase]
    public void ValidateParams_()
    {
        EthereumEcdsa ecdsa = new EthereumEcdsa(0);
        XdcSealValidator validator = new XdcSealValidator(Substitute.For<ISnapshotManager>(), Substitute.For<ISpecProvider>());

        XdcBlockHeader parent = Build.A.XdcBlockHeader().TestObject;
        XdcBlockHeader header = Build.A.XdcBlockHeader().TestObject;

        //Assert.That(validator.ValidateParams(parent, header, ), Is.False);
    }

}
