// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Stateless.Execution.IO;

/// <summary>The trusted-input format: a block and its execution witness laid out for a guest to use where they lie.</summary>
/// <remarks>
/// <para>
/// "Trusted" names who produces it, our own host, not what the guest assumes: the guest checks it exactly as it checks
/// the SSZ <c>StatelessInput</c>. The parent header is the last of a chain of witness headers, every state node is keyed
/// by its own keccak, every code is keyed by its keccak, every sender is recovered from its signature, and the block's
/// body roots and hash are checked against its header. What the format drops is the SSZ envelope, the merkleization of
/// the payload and the copies of the witness out of the input.
/// </para>
/// <para>
/// Layout, little-endian, every section and item 8-byte aligned from the start of the input:
/// <list type="bullet">
/// <item>A <see cref="HeaderLength"/>-byte header: magic, version, chain id, <see cref="ProtocolFork"/>, the offset and
/// length of the block header, withdrawals and block access list RLP, the offsets of the four item lists, and the
/// total length.</item>
/// <item>Item lists of transactions (as sent on the wire), witness headers, state nodes and codes: a count and a
/// reserved word, then the items. An item is its length, <see cref="ItemPrefixLength"/> - 4 reserved bytes, the item's
/// bytes and padding to 8; a code is followed by <see cref="CodeInfo.ExecutionPadding"/> more bytes before that padding.
/// The reserved bytes before an item are where a guest that has no moving collector lays the header of an array over
/// the item, so that the item is a <see cref="T:byte[]"/> without a copy.</item>
/// </list>
/// </para>
/// </remarks>
public static partial class TrustedInput
{
    /// <summary>The schema id a trusted input reports in its <see cref="StatelessValidationResult"/>.</summary>
    /// <remarks>Its <see cref="StatelessValidationResult.NewPayloadRequestRoot"/> then holds the block hash.</remarks>
    public const ushort SchemaId = 0x7401;

    public const uint Magic = 0x4E49544E; // "NTIN"
    public const uint Version = 1;
    public const int HeaderLength = 80;

    /// <summary>The bytes before every item: its length, then room for an array header.</summary>
    public const int ItemPrefixLength = 24;

    private const int ChainIdOffset = 8;
    private const int ForkOffset = 16;
    private const int BlockHeaderSection = 24;
    private const int WithdrawalsSection = 32;
    private const int BlockAccessListSection = 40;
    private const int TransactionsList = 56;
    private const int WitnessHeadersList = 60;
    private const int StateNodesList = 64;
    private const int CodesList = 68;
    private const int TotalLengthOffset = 72;
    private const int ListPrefixLength = 8;

    /// <summary>Encodes <paramref name="block"/> and <paramref name="witness"/> as a trusted input under the chain's own
    /// fork schedule.</summary>
    public static byte[] Write(ulong chainId, Block block, Witness witness)
    {
        byte[][] encodedTransactions = new byte[block.Transactions.Length][];
        for (int i = 0; i < encodedTransactions.Length; i++)
            encodedTransactions[i] = Rlp.Encode(block.Transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;

        return Write(chainId, ProtocolFork.Current, block, encodedTransactions, witness);
    }

    /// <summary>Encodes <paramref name="block"/> and <paramref name="witness"/> as a trusted input.</summary>
    /// <param name="encodedTransactions">The block's transactions as sent on the wire, in block order.</param>
    public static byte[] Write(ulong chainId, ProtocolFork fork, Block block, IReadOnlyList<byte[]> encodedTransactions, Witness witness)
    {
        byte[] header = Rlp.Encode(block.Header).Bytes;
        byte[] withdrawals = block.Withdrawals is null ? [] : Rlp.Encode(block.Withdrawals).Bytes;
        byte[] blockAccessList = block.EncodedBlockAccessList ?? [];

        int length = HeaderLength + Align(header.Length) + Align(withdrawals.Length) + Align(blockAccessList.Length)
            + ListLength(encodedTransactions, 0) + ListLength(witness.Headers, 0) + ListLength(witness.State, 0)
            + ListLength(witness.Codes, CodeInfo.ExecutionPadding);
        byte[] input = new byte[length];
        Span<byte> span = input;

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(span[ChainIdOffset..], chainId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ForkOffset..], (uint)fork);
        BinaryPrimitives.WriteUInt32LittleEndian(span[TotalLengthOffset..], (uint)length);

        int position = HeaderLength;
        position = WriteSection(span, BlockHeaderSection, position, header);
        position = WriteSection(span, WithdrawalsSection, position, withdrawals);
        position = WriteSection(span, BlockAccessListSection, position, blockAccessList);
        position = WriteList(span, TransactionsList, position, encodedTransactions, 0);
        position = WriteList(span, WitnessHeadersList, position, witness.Headers, 0);
        position = WriteList(span, StateNodesList, position, witness.State, 0);
        WriteList(span, CodesList, position, witness.Codes, CodeInfo.ExecutionPadding);

        return input;
    }

    /// <summary>Encodes the block and witness of a stateless input as a trusted input.</summary>
    /// <remarks>The engine API envelope, the versioned hashes and the claimed block hash, is not carried over: the
    /// trusted input binds the block through the hash of its header alone.</remarks>
    public static byte[] FromStatelessInput(ReadOnlySpan<byte> statelessInput)
    {
        StatelessPayload payload = InputDecoder.Decode(statelessInput);
        Block block = payload.GetBlock();
        using Witness witness = payload.Witness.ToWitness();
        return Write(payload.ChainId, (ProtocolFork)(payload.SchemaId >> 8), block, payload.EncodedTransactions, witness);
    }

    /// <summary>The RLP of the block header <paramref name="input"/> carries, whose keccak is the block hash a guest reports.</summary>
    public static ReadOnlySpan<byte> BlockHeaderOf(ReadOnlySpan<byte> input) => Section(input, BlockHeaderSection);

    /// <summary>The chain id <paramref name="input"/> declares.</summary>
    public static ulong ChainIdOf(ReadOnlySpan<byte> input) => BinaryPrimitives.ReadUInt64LittleEndian(input[ChainIdOffset..]);

    /// <summary>Reads a trusted input, using its items where they lie when the guest can.</summary>
    /// <remarks>Also installs the run's hash seed, the block hash, before anything downstream is keyed by a hash.</remarks>
    internal static TrustedPayload Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(data[TotalLengthOffset..]) != (uint)data.Length)
        {
            throw new ArgumentException("Not a trusted input", nameof(data));
        }

        ulong chainId = BinaryPrimitives.ReadUInt64LittleEndian(data[ChainIdOffset..]);
        ProtocolFork fork = (ProtocolFork)BinaryPrimitives.ReadUInt32LittleEndian(data[ForkOffset..]);

        ReadOnlySpan<byte> headerRlp = Section(data, BlockHeaderSection);
        Hash256 blockHash = Keccak.Compute(headerRlp);
        // The earliest point a commitment exists; decoding the header keys nothing.
        UInt256 seed = new(blockHash.Bytes, isBigEndian: true);
        SpanExtensions.SeedHashes(in seed);

        RlpReader headerReader = new(headerRlp);
        BlockHeader header = Rlp.GetDecoderOrThrow<BlockHeader>().Decode(ref headerReader)
            ?? throw new ArgumentException("No block header", nameof(data));
        headerReader.CheckEnd();
        // Stateless validation covers proof-of-stake blocks only, as for the engine API payload a stateless input carries.
        header.IsPostMerge = true;

        ReadOnlySpan<byte> withdrawalsRlp = Section(data, WithdrawalsSection);
        ReadOnlySpan<byte> blockAccessListRlp = Section(data, BlockAccessListSection);
        byte[][] transactions = ReadItems(data, TransactionsList, 0);
        Witness witness = new()
        {
            Headers = new ItemList(ReadItems(data, WitnessHeadersList, 0)),
            State = new ItemList(ReadItems(data, StateNodesList, 0)),
            Codes = ReadCodes(data),
            Keys = ArrayPoolList<byte[]>.Empty()
        };

        ForkActivation activation = new(header.Number, header.Timestamp);
        ISpecProvider specProvider = StatelessSpecProvider.Create(chainId, fork, activation);
        Withdrawal[]? withdrawals = withdrawalsRlp.IsEmpty ? null : DecodeWithdrawals(withdrawalsRlp);
        byte[]? encodedBlockAccessList = blockAccessListRlp.IsEmpty ? null : blockAccessListRlp.ToArray();

        return new TrustedPayload(
            GetBlock: () => CreateBlock(header, transactions, withdrawals, encodedBlockAccessList),
            Witness: witness,
            EncodedTransactions: transactions,
            ChainId: chainId,
            BlockHash: blockHash,
            SpecProvider: specProvider);
    }

    private static Block CreateBlock(BlockHeader header, byte[][] encodedTransactions, Withdrawal[]? withdrawals, byte[]? encodedBlockAccessList)
    {
        TransactionDecodingResult decoded = TxsDecoder.DecodeTxsBorrowingBuffers(encodedTransactions, skipErrors: false);
        if (decoded.Error is not null) throw new InvalidOperationException(decoded.Error);

        Block block = new(header, decoded.Transactions, [], withdrawals);
        if (encodedBlockAccessList is not null)
        {
            if (!ExecutionPayloadV4.TryDecodeBlockAccessList(encodedBlockAccessList, out ReadOnlyBlockAccessList? blockAccessList, out string? error))
                throw new InvalidOperationException(error);

            block.BlockAccessList = blockAccessList;
            block.EncodedBlockAccessList = encodedBlockAccessList;
        }

        return block;
    }

    private static Withdrawal[] DecodeWithdrawals(ReadOnlySpan<byte> rlp)
    {
        RlpReader reader = new(rlp);
        Withdrawal[] withdrawals = reader.DecodeArray(Rlp.GetDecoderOrThrow<Withdrawal>())!;
        reader.CheckEnd();
        return withdrawals;
    }

    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> data, int field)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data[field..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[(field + 4)..]);
        if (offset % 8 != 0 || offset < HeaderLength || (ulong)offset + length > (ulong)data.Length)
            throw new ArgumentException("Section out of bounds", nameof(data));

        return data.Slice((int)offset, (int)length);
    }

    /// <summary>Returns the item list whose offset the header holds at <paramref name="field"/>.</summary>
    /// <param name="padding">The bytes each item is followed by before its own padding to 8.</param>
    private static byte[][] ReadItems(ReadOnlySpan<byte> data, int field, int padding)
    {
        (int position, int count) = ListStart(data, field);
        byte[][] items = new byte[count][];
        for (int i = 0; i < items.Length; i++)
        {
            int length = ItemLength(data, position, padding);
            items[i] = Item(data, position + ItemPrefixLength, length);
            position += ItemStride(length, padding);
        }

        return items;
    }

    private static CodeList ReadCodes(ReadOnlySpan<byte> data)
    {
        (int position, int count) = ListStart(data, CodesList);
        ReadOnlyMemory<byte>[] codes = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < codes.Length; i++)
        {
            int length = ItemLength(data, position, CodeInfo.ExecutionPadding);
            codes[i] = Code(data, position + ItemPrefixLength, length);
            position += ItemStride(length, CodeInfo.ExecutionPadding);
        }

        return new CodeList(codes);
    }

    private static (int Position, int Count) ListStart(ReadOnlySpan<byte> data, int field)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data[field..]);
        if (offset % 8 != 0 || offset < HeaderLength || (ulong)offset + ListPrefixLength > (ulong)data.Length)
            throw new ArgumentException("Item list out of bounds", nameof(data));

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)offset..]);
        // Every item takes at least its prefix, which bounds the count before anything is allocated for it.
        if (count > (uint)(data.Length - (int)offset - ListPrefixLength) / ItemPrefixLength)
            throw new ArgumentException("Item count out of bounds", nameof(data));

        return ((int)offset + ListPrefixLength, (int)count);
    }

    private static int ItemLength(ReadOnlySpan<byte> data, int position, int padding)
    {
        if ((ulong)(uint)position + ItemPrefixLength > (ulong)data.Length)
            throw new ArgumentException("Item out of bounds", nameof(data));

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        if ((ulong)(uint)position + (ulong)ItemPrefixLength + length + (ulong)padding > (ulong)data.Length)
            throw new ArgumentException("Item out of bounds", nameof(data));

        return (int)length;
    }

    private static int ItemStride(int length, int padding) => ItemPrefixLength + Align(length + padding);

    private static int Align(int length) => (length + 7) & ~7;

    private static int ListLength(IReadOnlyList<byte[]> items, int padding)
    {
        int length = ListPrefixLength;
        foreach (byte[] item in items) length += ItemStride(item.Length, padding);
        return length;
    }

    private static int WriteSection(Span<byte> span, int field, int position, ReadOnlySpan<byte> section)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[field..], (uint)position);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(field + 4)..], (uint)section.Length);
        section.CopyTo(span[position..]);
        return position + Align(section.Length);
    }

    private static int WriteList(Span<byte> span, int field, int position, IReadOnlyList<byte[]> items, int padding)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[field..], (uint)position);
        BinaryPrimitives.WriteUInt32LittleEndian(span[position..], (uint)items.Count);
        position += ListPrefixLength;
        foreach (byte[] item in items)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[position..], (uint)item.Length);
            item.CopyTo(span[(position + ItemPrefixLength)..]);
            position += ItemStride(item.Length, padding);
        }

        return position;
    }

    /// <summary>The <paramref name="length"/>-byte item at <paramref name="start"/> as an array.</summary>
    /// <remarks>See <c>TrustedInput.std.cs</c> and <c>.zkevm.cs</c>.</remarks>
    private static partial byte[] Item(ReadOnlySpan<byte> data, int start, int length);

    /// <summary>The <paramref name="length"/>-byte code at <paramref name="start"/>, followed by its execution padding.</summary>
    /// <remarks>See <c>TrustedInput.std.cs</c> and <c>.zkevm.cs</c>.</remarks>
    private static partial ReadOnlyMemory<byte> Code(ReadOnlySpan<byte> data, int start, int length);

    private sealed class ItemList(byte[][] items) : IOwnedReadOnlyList<byte[]>
    {
        public int Count => items.Length;

        public byte[] this[int index] => items[index];

        public ReadOnlySpan<byte[]> AsSpan() => items;

        public IEnumerator<byte[]> GetEnumerator() => ((IEnumerable<byte[]>)items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { }
    }

    /// <summary>The codes as executable code memory, and as arrays copied on request.</summary>
    /// <remarks>The witness code store reads the memory, so the guest runs each code where it lies.</remarks>
    private sealed class CodeList(ReadOnlyMemory<byte>[] codes) : IOwnedReadOnlyList<byte[]>, IReadOnlyList<ReadOnlyMemory<byte>>
    {
        public int Count => codes.Length;

        public byte[] this[int index] => codes[index].ToArray();

        ReadOnlyMemory<byte> IReadOnlyList<ReadOnlyMemory<byte>>.this[int index] => codes[index];

        public ReadOnlySpan<byte[]> AsSpan()
        {
            byte[][] arrays = new byte[codes.Length][];
            for (int i = 0; i < arrays.Length; i++) arrays[i] = codes[i].ToArray();
            return arrays;
        }

        public IEnumerator<byte[]> GetEnumerator()
        {
            foreach (ReadOnlyMemory<byte> code in codes) yield return code.ToArray();
        }

        IEnumerator<ReadOnlyMemory<byte>> IEnumerable<ReadOnlyMemory<byte>>.GetEnumerator() =>
            ((IEnumerable<ReadOnlyMemory<byte>>)codes).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { }
    }
}

/// <param name="EncodedTransactions">The block's transactions as sent on the wire, which sender recovery hashes.</param>
/// <param name="GetBlock">Builds the block. Deferred, as it decodes attacker-controlled transactions and can throw.</param>
internal readonly record struct TrustedPayload(
    Func<Block> GetBlock,
    Witness Witness,
    byte[][] EncodedTransactions,
    ulong ChainId,
    Hash256 BlockHash,
    ISpecProvider SpecProvider);
