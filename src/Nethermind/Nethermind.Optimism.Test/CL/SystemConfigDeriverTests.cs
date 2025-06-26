// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class SystemConfigDeriverTests
{
    private static readonly Address SystemConfigProxy = new("0x229047fed2591dbec1eF1118d64F7aF3dB9EB290");

    private static readonly AbiSignature AddressSignature = new("address", AbiType.Address);
    private static readonly AbiSignature BytesSignature = new("bytes", AbiType.DynamicBytes);
    private static readonly AbiSignature UInt256Signature = new("oneUint256", AbiType.UInt256);
    private static readonly AbiSignature UInt256TupleSignature = new("twoUint256", AbiType.UInt256, AbiType.UInt256);

    private ReceiptForRpc[] BuildReceipts(byte[] data, Hash256 topic) =>
    [
        new()
        {
            Status = StatusCode.Success,
            Logs =
            [
                new()
                {
                    Address = SystemConfigProxy,
                    Topics = [SystemConfigUpdate.EventABIHash, SystemConfigUpdate.EventVersion0, topic],
                    Data = data,
                }
            ]
        }
    ];

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedBatcher()
    {
        var rawAddress = new byte[Address.Size];
        rawAddress[19] = 0xAA;
        var address = new Address(rawAddress);

        var encodedAddress = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, AddressSignature, address);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedAddress);

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.Batcher);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);
        var expectedConfig = new SystemConfig { BatcherAddress = address };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedFeeScalars()
    {
        UInt256 scalar = 0xAA;

        var encodedScalar = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256Signature, scalar);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedScalar);

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.FeeScalars);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

        var expectedConfig = new SystemConfig
        {
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

        var encodedScalar = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256Signature, scalar);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedScalar);

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.FeeScalars);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

        var expectedConfig = new SystemConfig
        {
            Scalar = scalarData,
            Overhead = new byte[32]
        };

        actualConfig.Should().Be(expectedConfig);
    }

    [TestCase(1)]
    [TestCase(8)]
    [TestCase(10)]
    [TestCase(12)]
    [TestCase(20)]
    [TestCase(23)]
    public void UpdateSystemConfigFromL1BLock_UpdatedFeeScalars_InvalidEcotone(int indexOfNonZero)
    {
        var scalarData = new byte[Hash256.Size];
        scalarData[0] = 1;
        scalarData[indexOfNonZero] = 0xFF;
        var scalar = new ValueHash256(scalarData).ToUInt256();

        var encodedScalar = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256Signature, scalar);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedScalar);

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.FeeScalars);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var initialConfig = new SystemConfig();
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(initialConfig, receipts);

        // Invalid scalars should be ignored and we should keep the initial config.
        actualConfig.Should().Be(initialConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_UpdatedUnsafeBlockSigner()
    {
        var rawAddress = new byte[Address.Size];
        rawAddress[19] = 0xAA;
        var address = new Address(rawAddress);

        var encodedAddress = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, AddressSignature, address);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedAddress);

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.UnsafeBlockSigner);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

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

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.GasLimit);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

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

        var receipts = BuildReceipts(encodedData, SystemConfigUpdate.EIP1559Params);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var actualConfig = deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

        var expectedConfig = new SystemConfig
        {
            EIP1559Params = [.. new byte[24], 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8]
        };

        actualConfig.Should().Be(expectedConfig);
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_InvalidTopics()
    {
        ReceiptForRpc[] receipts =
        [
            new()
            {
                Status = StatusCode.Success,
                Logs =
                [
                    new()
                    {
                        Address = SystemConfigProxy,
                        Topics = [SystemConfigUpdate.EventABIHash]
                    }
                ]
            }
        ];

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var update = () => deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

        update.Should().Throw<ArgumentException>();
    }

    [Test]
    public void UpdateSystemConfigFromL1BLock_InvalidTooManyBytes()
    {
        UInt256 overhead = 0xFF;
        UInt256 scalar = 0xAA;

        var encodedPair = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, UInt256TupleSignature, overhead, scalar);
        var encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, BytesSignature, encodedPair);

        var receipts = BuildReceipts([.. encodedData, 0x00], SystemConfigUpdate.FeeScalars);

        var deriver = new SystemConfigDeriver(SystemConfigProxy);
        var update = () => deriver.UpdateSystemConfigFromL1BLockReceipts(new SystemConfig(), receipts);

        update.Should().Throw<AbiException>();
    }
}
