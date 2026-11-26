// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class L1BlockInfoBuilderTests
{
    [Test]
    public void MainnetSystemTx()
    {
        byte[] data = Convert.FromHexString(
            "440a5e200000146b000f79c5000000000000000100000000678f36fb00000000014aac0100000000000000000000000000000000000000000000000000000001763888f90000000000000000000000000000000000000000000000000000000006ec6da8e3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd800000000000000000000000006887246668a3b87f54deb3b94ba47a6f63f32985");
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(data, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(l1BlockInfo.BlockHash, Is.EqualTo(new Hash256("0xe3c9e07f6666c25368058388c7be8ce0e66212ea2c237514932fbbbd58e2dd80")));
            Assert.That(l1BlockInfo.Number, Is.EqualTo(21670913));
            Assert.That(l1BlockInfo.BaseFee, Is.EqualTo((UInt256)6278383865));
        }

        CLChainSpecEngineParameters parameters = new() { OptimismPortalProxy = TestItem.AddressA };
        DepositTransactionBuilder depositTransactionBuilder = new(1, parameters);
        Transaction tx = depositTransactionBuilder.BuildL1InfoTransaction(l1BlockInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.Data, Is.SequenceEqualTo(data));
            Assert.That(tx.SourceHash, Is.EqualTo(new Hash256("0x0a17e1f9443295ceee6678f1fc70aa6e45d2988cfbe062ec28db9f1e5fcb469d")));
        }
    }

    [TestCase(MainnetSpecProvider.CancunBlockTimestamp, "32594247408339235274")]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp - 1, "32594247408339235274")]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp, "10203738961070")]
    [TestCase(MainnetSpecProvider.BPO1BlockTimestamp, "63863970")]
    [TestCase(MainnetSpecProvider.BPO2BlockTimestamp, "375995")]
    public void FromL1BlockAndSystemConfig_uses_blob_fee_update_fraction_of_l1_block(ulong l1Timestamp, string expectedBlobBaseFee)
    {
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(
            CreateL1Block(l1Timestamp, 150_000_000), new SystemConfig(), 0, MainnetSpecProvider.Instance);

        Assert.That(l1BlockInfo.BlobBaseFee, Is.EqualTo(UInt256.Parse(expectedBlobBaseFee)));
    }

    [TestCase(MainnetSpecProvider.PragueBlockTimestamp + 99, "32594247408339235274")]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp + 100, "10203738961070")]
    public void FromL1BlockAndSystemConfig_uses_cancun_fraction_before_pectra_blob_schedule(ulong l1Timestamp, string expectedBlobBaseFee)
    {
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(
            CreateL1Block(l1Timestamp, 150_000_000), new SystemConfig(), 0, MainnetSpecProvider.Instance,
            pectraBlobScheduleTimestamp: MainnetSpecProvider.PragueBlockTimestamp + 100);

        Assert.That(l1BlockInfo.BlobBaseFee, Is.EqualTo(UInt256.Parse(expectedBlobBaseFee)));
    }

    [TestCase(null, "10203738961070")]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp + 100, "32594247408339235274")]
    public void Derived_payload_attributes_use_the_pectra_blob_schedule_of_the_chain_spec(ulong? pectraBlobScheduleTimestamp, string expectedBlobBaseFee)
    {
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder());
        CLChainSpecEngineParameters engineParameters = new() { OptimismPortalProxy = TestItem.AddressA, PectraBlobScheduleTimestamp = pectraBlobScheduleTimestamp };
        PayloadAttributesDeriver deriver = Driver.CreatePayloadAttributesDeriver(Substitute.For<ISystemConfigDeriver>(), engineParameters, 1, MainnetSpecProvider.Instance);
        L1Block l1Origin = CreateL1Block(MainnetSpecProvider.PragueBlockTimestamp + 99, 150_000_000);
        PayloadAttributesRef parent = new()
        {
            Number = 1,
            SystemConfig = new SystemConfig(),
            L1BlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(l1Origin, new SystemConfig(), 0, MainnetSpecProvider.Instance),
            PayloadAttributes = new OptimismPayloadAttributes(),
        };
        SingularBatch batch = new() { IsFirstBlockInEpoch = false, EpochNumber = 1, Timestamp = MainnetSpecProvider.PragueBlockTimestamp + 101, Transactions = [] };

        PayloadAttributesRef? derived = deriver.TryDerivePayloadAttributes(batch, parent, l1Origin, []);

        Assert.That(derived!.L1BlockInfo.BlobBaseFee, Is.EqualTo(UInt256.Parse(expectedBlobBaseFee)));
    }

    [Test]
    public void FromL1BlockAndSystemConfig_uses_blob_base_fee_of_one_without_excess_blob_gas()
    {
        L1Block block = new()
        {
            Hash = TestItem.KeccakA,
            Number = 1,
            Timestamp = MainnetSpecProvider.CancunBlockTimestamp - 1,
            BaseFeePerGas = 1,
        };

        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL1BlockAndSystemConfig(block, new SystemConfig(), 0, MainnetSpecProvider.Instance);

        Assert.That(l1BlockInfo.BlobBaseFee, Is.EqualTo(UInt256.One));
    }

    [Test]
    public void FromL1BlockAndSystemConfig_throws_when_blob_base_fee_overflows() =>
        Assert.That(
            () => L1BlockInfoBuilder.FromL1BlockAndSystemConfig(
                CreateL1Block(MainnetSpecProvider.BPO2BlockTimestamp, ulong.MaxValue), new SystemConfig(), 0, MainnetSpecProvider.Instance),
            Throws.InvalidOperationException);

    private static L1Block CreateL1Block(ulong timestamp, ulong excessBlobGas) => new()
    {
        Hash = TestItem.KeccakA,
        Number = 1,
        Timestamp = timestamp,
        BaseFeePerGas = 1,
        ExcessBlobGas = excessBlobGas,
    };
}
