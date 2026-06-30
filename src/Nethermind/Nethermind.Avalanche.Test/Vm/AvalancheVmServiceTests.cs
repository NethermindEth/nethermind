// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Avalanche.Blocks;
using Nethermind.Avalanche.Parity;
using Nethermind.Avalanche.Vm;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

// Generated from proto/vm/vm.proto (package "vm" => C# namespace "Vm").
using VmPb = global::Vm;

namespace Nethermind.Avalanche.Test.Vm;

/// <summary>
/// In-process unit tests for the wired <see cref="AvalancheVmService"/> block RPCs (<c>ParseBlock</c> and
/// <c>BlockVerify</c>): encode a Coreth <c>extblock</c> with the real codec, feed the bytes through the gRPC
/// service, and assert the response. No subprocess and no gRPC channel are involved — the service is invoked
/// directly, which is valid because these RPCs do not use their <see cref="ServerCallContext"/>.
/// </summary>
/// <remarks>
/// This lives in <c>Nethermind.Avalanche.Test</c> rather than <c>Nethermind.Avalanche.Vm.Test</c> on purpose:
/// the Vm.Test project generates its own client-side <c>Vm.*</c> protobuf types and links the VM as a subprocess
/// (<c>ReferenceOutputAssembly="false"</c>) for the handshake test, so it cannot also link the VM assembly. This
/// project generates no protos, so the VM assembly's server-side <c>Vm.*</c> types resolve uniquely.
/// </remarks>
public sealed class AvalancheVmServiceTests
{
    private static readonly Address AddressA = new("0x0000000000000000000000000000000000000aaa");
    private static readonly Address AddressB = new("0x0000000000000000000000000000000000000bbb");

    private const ulong BlockNumber = 123;
    private const ulong BlockTimestamp = 1_700_000_000;

    private static AvalancheBlockHeader BuildHeader() => new(
        Keccak.Compute("parent"),
        Keccak.OfAnEmptySequenceRlp,
        Address.Zero,
        (UInt256)1_000_000,
        number: BlockNumber,
        gasLimit: 8_000_000,
        timestamp: BlockTimestamp,
        extraData: [])
    {
        StateRoot = Keccak.Compute("state"),
        TxRoot = Keccak.Compute("txs"),
        ReceiptsRoot = Keccak.Compute("receipts"),
        Bloom = Bloom.Empty,
        GasUsed = 42_000,
        MixHash = Keccak.Compute("mix"),
        Nonce = 7,
        ExtDataHash = (Hash256)AvalancheExtData.EmptyExtDataHash,
        BaseFeePerGas = (UInt256)25_000_000_000,
        ExtDataGasUsed = (UInt256)0,
        BlockGasCost = (UInt256)10_000
    };

    private static Transaction BuildLegacyTx(ulong nonce, ulong gasLimit, UInt256 value, Address to) => new()
    {
        Type = TxType.Legacy,
        Nonce = nonce,
        GasPrice = (UInt256)20_000_000_000,
        GasLimit = gasLimit,
        To = to,
        Value = value,
        Data = Array.Empty<byte>(),
        // A deterministic non-zero legacy signature (v = 27) so the tx encodes/decodes round-trip.
        Signature = new Signature((UInt256)1, (UInt256)2, 27)
    };

    private static AvalancheBlock BuildBlock()
    {
        Transaction[] txs =
        [
            BuildLegacyTx(0, 21_000, (UInt256)1_000, AddressA),
            BuildLegacyTx(1, 50_000, (UInt256)0, AddressB)
        ];

        AvalancheBlockBody body = new(txs, uncles: [], version: 3, extData: [0xde, 0xad, 0xbe, 0xef]);
        return new AvalancheBlock(BuildHeader(), body);
    }

    /// <summary>Builds a block whose body carries <paramref name="extData"/> and whose header commits to
    /// <paramref name="extDataHash"/>, so the caller can construct both consistent and tampered blocks.</summary>
    private static AvalancheBlock BuildBlockWithExtData(byte[] extData, Hash256 extDataHash)
    {
        AvalancheBlockHeader header = BuildHeader();
        header.ExtDataHash = extDataHash;
        AvalancheBlockBody body = new([], uncles: [], version: 0, extData);
        return new AvalancheBlock(header, body);
    }

    private static byte[] Encode(AvalancheBlock block) => AvalancheBlockDecoder.Instance.Encode(block);

    [Test]
    public void ParseBlock_maps_decoded_header_onto_the_response()
    {
        AvalancheBlock block = BuildBlock();
        byte[] encoded = Encode(block);
        Hash256 expectedId = AvalancheHeaderDecoder.Instance.ComputeHash(block.Header);

        AvalancheVmService service = new();
        VmPb.ParseBlockResponse response =
            service.ParseBlock(new VmPb.ParseBlockRequest { Bytes = ByteString.CopyFrom(encoded) }, context: null!).Result;

        Assert.That(new Hash256(response.Id.ToByteArray()), Is.EqualTo(expectedId),
            "block id must be keccak256(RLP(header)).");
        Assert.That(new Hash256(response.ParentId.ToByteArray()), Is.EqualTo(block.Header.ParentHash),
            "parent_id must be the header ParentHash.");
        Assert.That(response.Height, Is.EqualTo(BlockNumber), "height must be the header Number.");
        Assert.That(response.Timestamp.Seconds, Is.EqualTo((long)BlockTimestamp),
            "timestamp must be the header Time in unix seconds.");
        Assert.That(response.VerifyWithContext, Is.False);
    }

    [Test]
    public void ParseBlock_throws_InvalidArgument_on_malformed_bytes()
    {
        AvalancheVmService service = new();

        RpcException ex = Assert.ThrowsAsync<RpcException>(async () =>
            await service.ParseBlock(
                new VmPb.ParseBlockRequest { Bytes = ByteString.CopyFrom([0x01, 0x02, 0x03]) }, context: null!))!;

        Assert.That(ex.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void BlockVerify_accepts_empty_ext_data_committed_by_empty_hash()
    {
        AvalancheBlock block = BuildBlockWithExtData([], (Hash256)AvalancheExtData.EmptyExtDataHash);
        AvalancheVmService service = new();

        VmPb.BlockVerifyResponse response =
            service.BlockVerify(new VmPb.BlockVerifyRequest { Bytes = ByteString.CopyFrom(Encode(block)) }, context: null!).Result;

        Assert.That(response.Timestamp.Seconds, Is.EqualTo((long)BlockTimestamp));
    }

    [Test]
    public void BlockVerify_accepts_consistent_non_empty_ext_data_hash()
    {
        byte[] extData = [0xde, 0xad, 0xbe, 0xef];
        AvalancheBlock block = BuildBlockWithExtData(extData, (Hash256)AvalancheExtData.CalcExtDataHash(extData));
        AvalancheVmService service = new();

        VmPb.BlockVerifyResponse response =
            service.BlockVerify(new VmPb.BlockVerifyRequest { Bytes = ByteString.CopyFrom(Encode(block)) }, context: null!).Result;

        Assert.That(response.Timestamp.Seconds, Is.EqualTo((long)BlockTimestamp));
    }

    [Test]
    public void BlockVerify_rejects_inconsistent_ext_data_hash()
    {
        // Body carries real atomic data but the header still commits to the empty-extData hash: a tampered block.
        AvalancheBlock block = BuildBlockWithExtData([0xde, 0xad, 0xbe, 0xef], (Hash256)AvalancheExtData.EmptyExtDataHash);
        AvalancheVmService service = new();

        RpcException ex = Assert.ThrowsAsync<RpcException>(async () =>
            await service.BlockVerify(
                new VmPb.BlockVerifyRequest { Bytes = ByteString.CopyFrom(Encode(block)) }, context: null!))!;

        Assert.That(ex.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void BlockVerify_throws_InvalidArgument_on_malformed_bytes()
    {
        AvalancheVmService service = new();

        RpcException ex = Assert.ThrowsAsync<RpcException>(async () =>
            await service.BlockVerify(
                new VmPb.BlockVerifyRequest { Bytes = ByteString.CopyFrom([0x01, 0x02, 0x03]) }, context: null!))!;

        Assert.That(ex.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void Initialize_returns_mainnet_genesis_as_last_accepted()
    {
        AvalancheVmService service = new();
        VmPb.InitializeResponse response = service.Initialize(
            new VmPb.InitializeRequest { GenesisBytes = ByteString.CopyFrom(LoadMainnetGenesisBytes()) }, context: null!).Result;

        Hash256 genesisHash = new("0x31ced5b9beb7f8782b014660da0cb18cc409f121f408186886e1ca3e8eeca96b");
        Assert.That(new Hash256(response.LastAcceptedId.ToByteArray()), Is.EqualTo(genesisHash));
        Assert.That(response.Height, Is.EqualTo(0UL));
        Assert.That(service.Genesis!.ChainId, Is.EqualTo(43114));

        // The returned block bytes must parse back to a block whose header hashes to the genesis id.
        AvalancheBlock parsed = AvalancheBlockDecoder.Instance.Decode(response.Bytes.ToByteArray())!;
        Assert.That(AvalancheHeaderDecoder.Instance.ComputeHash(parsed.Header), Is.EqualTo(genesisHash));
    }

    [Test]
    public void Initialize_without_genesis_bytes_is_invalid()
    {
        AvalancheVmService service = new();

        RpcException ex = Assert.ThrowsAsync<RpcException>(async () =>
            await service.Initialize(new VmPb.InitializeRequest { GenesisBytes = ByteString.Empty }, context: null!))!;

        Assert.That(ex.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    private static byte[] LoadMainnetGenesisBytes()
    {
        Assembly assembly = typeof(AvalancheVmServiceTests).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(n => n.Contains("cchain-genesis", StringComparison.OrdinalIgnoreCase));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
