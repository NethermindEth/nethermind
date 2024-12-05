// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Optimism.CL;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class SystemConfigDeriverTests
{
    private static readonly Address L1SystemConfigAddress = new("0x229047fed2591dbec1eF1118d64F7aF3dB9EB290");

    private static readonly AbiSignature AddressSignature = new("address", AbiType.Address);
    private static readonly AbiSignature BytesSignature = new("bytes", AbiType.DynamicBytes);
    private static readonly AbiSignature UInt256Signature = new("oneUint256", AbiType.UInt256);
    private static readonly AbiSignature UInt256TupleSignature = new("twoUint256", AbiType.UInt256, AbiType.UInt256);

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedBatcher()
    {
        var rawAddress = new byte[Address.Size];
        rawAddress[19] = 0xAA;
        var address = new Address(rawAddress);

        var encodedAddress = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, AddressSignature, address);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedAddress);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.Batcher)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            Substitute.For<IOptimismSpecHelper>()
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);
        var expectedConfig = new SystemConfig { BatcherAddress = address };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedFeeScalars()
    {
        UInt256 overhead = 0xFF;
        UInt256 scalar = 0xAA;

        var encodedPair = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256TupleSignature, overhead, scalar);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedPair);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.FeeScalars)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            Substitute.For<IOptimismSpecHelper>()
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);

        var expectedConfig = new SystemConfig
        {
            Overhead = [.. new byte[31], 0xFF],
            Scalar = [.. new byte[31], 0xAA]
        };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedFeeScalars_Ecotone()
    {
        var scalarData = new byte[Hash256.Size];
        scalarData[0] = 1;
        scalarData[24 + 3] = 0xB3;
        scalarData[28 + 3] = 0xBB;
        var scalar = new ValueHash256(scalarData).ToUInt256();
        var overhead = 0xFF;

        var encodedPair = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256TupleSignature, overhead, scalar);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedPair);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var specHelper = Substitute.For<IOptimismSpecHelper>();
        specHelper.IsEcotone(blockHeader).Returns(true);

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.FeeScalars)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            specHelper
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);

        var expectedConfig = new SystemConfig
        {
            Scalar = scalarData,
            Overhead = new byte[32]
        };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedUnsafeBlockSigner()
    {
        var rawAddress = new byte[Address.Size];
        rawAddress[19] = 0xAA;
        var address = new Address(rawAddress);

        var encodedAddress = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, AddressSignature, address);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedAddress);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var specHelper = Substitute.For<IOptimismSpecHelper>();

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.UnsafeBlockSigner)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            specHelper
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);

        var expectedConfig = new SystemConfig();

        // The log data is ignored by consensus and no modifications to the
        // system config occur.
        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedGasLimit()
    {
        UInt256 gasLimit = 0xBB;

        var encodedGasLimit = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256Signature, gasLimit);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedGasLimit);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var specHelper = Substitute.For<IOptimismSpecHelper>();

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.GasLimit)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            specHelper
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);

        var expectedConfig = new SystemConfig
        {
            GasLimit = 0xBB
        };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedEIP1559Params()
    {
        byte[] eip1559ParamsRaw = [0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8];
        var eip1559Params = new UInt256(eip1559ParamsRaw, isBigEndian: true);

        var encodedEIP1559Params = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256Signature, eip1559Params);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedEIP1559Params);

        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var specHelper = Substitute.For<IOptimismSpecHelper>();

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithData(encodedData)
                        .WithTopics(
                            SystemConfigUpdate.EventABIHash,
                            SystemConfigUpdate.EventVersion0,
                            SystemConfigUpdate.EIP1559Params)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            specHelper
        );
        var actualConfig = deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);

        var expectedConfig = new SystemConfig
        {
            EIP1559Params = [.. new byte[24], 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8]
        };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_InvalidTopics()
    {
        var blockHeader = Build.A.BlockHeader
            .WithHash(TestItem.KeccakA)
            .TestObject;

        var specHelper = Substitute.For<IOptimismSpecHelper>();

        var receiptFinder = Substitute.For<IReceiptFinder>();
        receiptFinder.Get(TestItem.KeccakA).Returns([
            new TxReceipt
            {
                StatusCode = StatusCode.Success,
                Logs =
                [
                    Build.A.LogEntry
                        .WithAddress(L1SystemConfigAddress)
                        .WithTopics(SystemConfigUpdate.EventABIHash)
                        .TestObject
                ]
            }
        ]);

        var deriver = new SystemConfigDeriver(
            new RollupConfig { L1SystemConfigAddress = L1SystemConfigAddress },
            receiptFinder,
            specHelper
        );
        var update = () => deriver.UpdateSystemConfigFromL1BLock(new SystemConfig(), blockHeader);

        update.Should().Throw<ArgumentException>();
    }
}
