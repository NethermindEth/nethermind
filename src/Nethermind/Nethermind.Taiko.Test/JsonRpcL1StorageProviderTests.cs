// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
[NonParallelizable]
public class JsonRpcL1StorageProviderTests
{
    private const ulong AnchorBlockIdV3 = 1_000_000UL;
    private const ulong AnchorBlockIdV2 = 2_000_000UL;
    private const ulong AnchorBlockIdV1 = 3_000_000UL;

    private static readonly UInt256 StorageValue1 = (UInt256)0x42;
    private static readonly UInt256 StorageValue2 = (UInt256)0xab;
    private static readonly UInt256 StorageValue3 = (UInt256)0xcd;
    private static readonly UInt256 StorageValueLarge = (UInt256)0x1234567890abcdefUL;

    private IJsonRpcClient _mockRpcClient = null!;
    private IJsonSerializer _jsonSerializer = null!;
    private ILogManager _logManager = null!;
    private JsonRpcL1StorageProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRpcClient = Substitute.For<IJsonRpcClient>();
        _jsonSerializer = new EthereumJsonSerializer();
        _logManager = LimboLogs.Instance;
        _provider = new JsonRpcL1StorageProvider("http://localhost:8545", _jsonSerializer, _logManager);

        var rpcClientField = typeof(JsonRpcL1StorageProvider).GetField("_rpcClient",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        rpcClientField?.SetValue(_provider, _mockRpcClient);

        ClearAnchorBlockId();
    }

    [TearDown]
    public void TearDown()
    {
        ClearAnchorBlockId();
        _mockRpcClient.ClearReceivedCalls();
    }

    private static void ClearAnchorBlockId()
    {
        var anchorBlockIdField = typeof(JsonRpcL1StorageProvider).GetField("_anchorBlockId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        anchorBlockIdField?.SetValue(null, null);
    }

    [Test]
    public void ExtractAnchorBlockId_FromAnchorV3Transaction_ShouldExtractCorrectly()
    {
        Transaction anchorTx = Build.A.Transaction.WithData(CreateAnchorV3Data(AnchorBlockIdV3)).TestObject;
        JsonRpcL1StorageProvider.SetAnchorBlockId(anchorTx);

        SetupMockRpcResponse(StorageValue1);
        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().Be(StorageValue1);
        VerifyRpcCall(TestItem.AddressA, UInt256.Zero, (UInt256)AnchorBlockIdV3);
    }

    [Test]
    public void ExtractAnchorBlockId_FromAnchorV2Transaction_ShouldExtractCorrectly()
    {
        Transaction anchorTx = Build.A.Transaction.WithData(CreateAnchorV2Data(AnchorBlockIdV2)).TestObject;
        JsonRpcL1StorageProvider.SetAnchorBlockId(anchorTx);

        SetupMockRpcResponse(StorageValue2);
        UInt256? result = _provider.GetStorageValue(TestItem.AddressB, UInt256.One);

        result.Should().Be(StorageValue2);
        VerifyRpcCall(TestItem.AddressB, UInt256.One, (UInt256)AnchorBlockIdV2);
    }

    [Test]
    public void ExtractAnchorBlockId_FromAnchorTransaction_ShouldExtractCorrectly()
    {
        Transaction anchorTx = Build.A.Transaction.WithData(CreateAnchorData(AnchorBlockIdV1)).TestObject;
        JsonRpcL1StorageProvider.SetAnchorBlockId(anchorTx);

        SetupMockRpcResponse(StorageValue3);
        UInt256? result = _provider.GetStorageValue(TestItem.AddressC, (UInt256)2);

        result.Should().Be(StorageValue3);
        VerifyRpcCall(TestItem.AddressC, (UInt256)2, (UInt256)AnchorBlockIdV1);
    }

    [Test]
    public void GetStorageValue_WhenAnchorBlockIdNotSet_ShouldReturnNull()
    {
        ClearAnchorBlockId();

        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().BeNull();
        _mockRpcClient.DidNotReceive().Post<string>(Arg.Any<string>(), Arg.Any<object[]>());
    }

    [Test]
    public void GetStorageValue_WhenRpcReturnsNull_ShouldReturnNull()
    {
        SetAnchorBlockId(AnchorBlockIdV3);

        _mockRpcClient.Post<string>("eth_getStorageAt", Arg.Any<object[]>())
            .Returns(Task.FromResult<string?>(null));

        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().BeNull();
    }

    [Test]
    public void GetStorageValue_WhenRpcThrowsException_ShouldReturnNull()
    {
        SetAnchorBlockId(AnchorBlockIdV3);

        _mockRpcClient.Post<string>("eth_getStorageAt", Arg.Any<object[]>())
            .Returns(Task.FromException<string?>(new Exception("RPC error")));

        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().BeNull();
    }

    [Test]
    public void GetStorageValue_WithValidResponse_ShouldParseAndReturnValue()
    {
        SetAnchorBlockId(AnchorBlockIdV3);

        SetupMockRpcResponse(StorageValueLarge);
        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().Be(StorageValueLarge);
    }

    [Test]
    public void ExtractAnchorBlockId_WithInvalidTransactionData_ShouldReturnNull()
    {
        Transaction shortTx = Build.A.Transaction.WithData(new byte[10]).TestObject;
        JsonRpcL1StorageProvider.SetAnchorBlockId(shortTx);

        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().BeNull();
        _mockRpcClient.DidNotReceive().Post<string>(Arg.Any<string>(), Arg.Any<object[]>());
    }

    [Test]
    public void ExtractAnchorBlockId_WithUnknownSelector_ShouldReturnNull()
    {
        byte[] unknownData = new byte[100];
        unknownData[0] = 0x12;
        unknownData[1] = 0x34;
        unknownData[2] = 0x56;
        unknownData[3] = 0x78;

        Transaction unknownTx = Build.A.Transaction.WithData(unknownData).TestObject;
        JsonRpcL1StorageProvider.SetAnchorBlockId(unknownTx);

        UInt256? result = _provider.GetStorageValue(TestItem.AddressA, UInt256.Zero);

        result.Should().BeNull();
        _mockRpcClient.DidNotReceive().Post<string>(Arg.Any<string>(), Arg.Any<object[]>());
    }

    private void SetAnchorBlockId(ulong blockId)
    {
        Transaction anchorTx = Build.A.Transaction.WithData(CreateAnchorV3Data(blockId)).TestObject;
        JsonRpcL1StorageProvider.SetAnchorBlockId(anchorTx);
    }

    private void SetupMockRpcResponse(UInt256 value)
    {
        _mockRpcClient.Post<string>("eth_getStorageAt", Arg.Any<object[]>())
            .Returns(Task.FromResult<string?>(value.ToHexString(true)));
    }

    private void VerifyRpcCall(Address contractAddress, UInt256 storageKey, UInt256 anchorBlockId)
    {
        _mockRpcClient.Received(1).Post<string>("eth_getStorageAt", Arg.Is<object[]>(args =>
            args[0].ToString() == contractAddress.ToString() &&
            args[1].ToString() == storageKey.ToHexString(true) &&
            args[2].ToString() == anchorBlockId.ToHexString(true)
        ));
    }

    private static byte[] CreateAnchorV3Data(ulong anchorBlockId)
    {
        const int SelectorBytes = 4;
        const int SlotBytes = 32;
        const int Uint64Bytes = 8;
        const int Uint64Padding = SlotBytes - Uint64Bytes;

        byte[] data = new byte[100];
        Array.Copy(TaikoBlockValidator.AnchorV3Selector, 0, data, 0, SelectorBytes);

        byte[] blockIdBytes = new byte[Uint64Bytes];
        BinaryPrimitives.WriteUInt64BigEndian(blockIdBytes, anchorBlockId);
        Array.Copy(blockIdBytes, 0, data, SelectorBytes + Uint64Padding, Uint64Bytes);

        return data;
    }

    private static byte[] CreateAnchorV2Data(ulong anchorBlockId)
    {
        const int SelectorBytes = 4;
        const int SlotBytes = 32;
        const int Uint64Bytes = 8;
        const int Uint64Padding = SlotBytes - Uint64Bytes;

        byte[] data = new byte[100];
        Array.Copy(TaikoBlockValidator.AnchorV2Selector, 0, data, 0, SelectorBytes);

        byte[] blockIdBytes = new byte[Uint64Bytes];
        BinaryPrimitives.WriteUInt64BigEndian(blockIdBytes, anchorBlockId);
        Array.Copy(blockIdBytes, 0, data, SelectorBytes + Uint64Padding, Uint64Bytes);

        return data;
    }

    private static byte[] CreateAnchorData(ulong l1BlockId)
    {
        const int SelectorBytes = 4;
        const int SlotBytes = 32;
        const int Uint64Bytes = 8;
        const int Uint64Padding = SlotBytes - Uint64Bytes;

        byte[] data = new byte[100];
        Array.Copy(TaikoBlockValidator.AnchorSelector, 0, data, 0, SelectorBytes);

        byte[] blockIdBytes = new byte[Uint64Bytes];
        BinaryPrimitives.WriteUInt64BigEndian(blockIdBytes, l1BlockId);
        Array.Copy(blockIdBytes, 0, data, SelectorBytes + (2 * SlotBytes) + Uint64Padding, Uint64Bytes);

        return data;
    }
}

