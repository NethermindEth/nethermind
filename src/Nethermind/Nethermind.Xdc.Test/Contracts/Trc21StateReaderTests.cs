// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class Trc21StateReaderTests
{
    private static readonly Address Issuer = new("0x8c0faeb5C6bEd2129b8674F262Fd45c4e9468bee");

    [Test]
    public void GetFeeCapacities_ReadsIssuerTokenCapacity()
    {
        XdcBlockHeader header = (XdcBlockHeader)Build.A.XdcBlockHeader().WithStateRoot(Keccak.Compute("trc21-root")).TestObject;
        Address tokenA = new("0x00000000000000000000000000000000000000a1");
        Address tokenB = new("0x00000000000000000000000000000000000000b2");

        Dictionary<(Address Contract, UInt256 Slot), UInt256> storage = new()
        {
            [(Issuer, (UInt256)1)] = 2,
            [(Issuer, CalculateDynamicArrayElementSlot(1, 0))] = new UInt256(tokenA.ToHash().BytesAsSpan, isBigEndian: true),
            [(Issuer, CalculateDynamicArrayElementSlot(1, 1))] = new UInt256(tokenB.ToHash().BytesAsSpan, isBigEndian: true),
            [(Issuer, CalculateMappingSlot(tokenA.ToHash(), 2))] = 11,
            [(Issuer, CalculateMappingSlot(tokenB.ToHash(), 2))] = 22,
        };

        IStateReader stateReader = CreateStateReader(storage, out _);
        var sut = new Trc21StateReader(stateReader, CreateSpecProvider());

        IReadOnlyDictionary<Address, UInt256> result = sut.GetFeeCapacities(header);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[tokenA], Is.EqualTo((UInt256)11));
        Assert.That(result[tokenB], Is.EqualTo((UInt256)22));
    }

    [Test]
    public void ValidateTransaction_Transfer_RequiresMinFeePlusTransferAmount()
    {
        XdcBlockHeader header = (XdcBlockHeader)Build.A.XdcBlockHeader().WithStateRoot(Keccak.Compute("validate-root")).TestObject;
        Address token = new("0x00000000000000000000000000000000000000a1");
        Address sender = new("0x00000000000000000000000000000000000000f1");

        UInt256 balanceSlot = CalculateMappingSlot(sender.ToHash(), 0);
        Dictionary<(Address Contract, UInt256 Slot), UInt256> storage = new()
        {
            [(token, balanceSlot)] = 110,
            [(token, (UInt256)1)] = 10,
        };

        IStateReader stateReader = CreateStateReader(storage, out _);
        var sut = new Trc21StateReader(stateReader, CreateSpecProvider());

        Assert.That(sut.ValidateTransaction(header, sender, token, BuildTransferData(100)), Is.True);
        Assert.That(sut.ValidateTransaction(header, sender, token, BuildTransferData(101)), Is.False);
    }

    [Test]
    public void ValidateTransaction_ZeroBalance_ReturnsTrueForParity()
    {
        XdcBlockHeader header = (XdcBlockHeader)Build.A.XdcBlockHeader().WithStateRoot(Keccak.Compute("zero-balance")).TestObject;
        Address token = new("0x00000000000000000000000000000000000000a1");
        Address sender = new("0x00000000000000000000000000000000000000f1");

        IStateReader stateReader = CreateStateReader(new Dictionary<(Address Contract, UInt256 Slot), UInt256>(), out _);
        var sut = new Trc21StateReader(stateReader, CreateSpecProvider());

        Assert.That(sut.ValidateTransaction(header, sender, token, BuildTransferData(100)), Is.True);
    }

    private static IStateReader CreateStateReader(
        Dictionary<(Address Contract, UInt256 Slot), UInt256> storage,
        out ReadCounter reads)
    {
        var fakeStorage = new Dictionary<(Address Contract, UInt256 Slot), byte[]>();
        foreach (((Address contract, UInt256 slot), UInt256 value) in storage)
        {
            fakeStorage[(contract, slot)] = value.ToBigEndian().WithoutLeadingZeros().ToArray();
        }

        var counter = new ReadCounter();
        reads = counter;
        return new FakeStateReader(fakeStorage, counter);
    }

    private sealed class ReadCounter
    {
        public int Count { get; set; }
    }

    private sealed class FakeStateReader(
        Dictionary<(Address Contract, UInt256 Slot), byte[]> storage,
        ReadCounter counter) : IStateReader
    {
        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            account = default;
            return false;
        }

        public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
        {
            counter.Count++;
            return storage.TryGetValue((address, index), out byte[]? value) ? value : [];
        }

        public byte[]? GetCode(Hash256 codeHash) => throw new NotSupportedException();
        public byte[]? GetCode(in ValueHash256 codeHash) => throw new NotSupportedException();

        public void RunTreeVisitor<TCtx>(
            ITreeVisitor<TCtx> treeVisitor,
            BlockHeader? baseBlock,
            VisitingOptions? visitingOptions = null)
            where TCtx : struct, INodeContext<TCtx>
            => throw new NotSupportedException();

        public bool HasStateForBlock(BlockHeader? baseBlock) => true;
    }

    private static ISpecProvider CreateSpecProvider()
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.Trc21IssuerContract.Returns(Issuer);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        return specProvider;
    }

    private static UInt256 CalculateDynamicArrayElementSlot(ulong slot, ulong index)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        new UInt256(slot).ToBigEndian(slotBytes);
        UInt256 baseSlot = new UInt256(Keccak.Compute(slotBytes).Bytes, isBigEndian: true);
        return baseSlot + index;
    }

    private static UInt256 CalculateMappingSlot(in ValueHash256 key, ulong slot)
    {
        Span<byte> input = stackalloc byte[64];
        key.BytesAsSpan.CopyTo(input);
        new UInt256(slot).ToBigEndian(input[32..]);
        return new UInt256(Keccak.Compute(input).Bytes, isBigEndian: true);
    }

    private static byte[] BuildTransferData(UInt256 value)
    {
        byte[] data = new byte[68];
        data[0] = 0xa9;
        data[1] = 0x05;
        data[2] = 0x9c;
        data[3] = 0xbb;
        value.ToBigEndian(data.AsSpan(36, 32));
        return data;
    }
}
