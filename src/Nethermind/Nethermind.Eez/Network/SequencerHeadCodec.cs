// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Snappier;

namespace Nethermind.Eez.Network;

internal readonly record struct SequencerHeadClaim(
    ulong RollupId, ulong Number, Hash256 BlockHash, Hash256 ParentHash, ulong EpochMs, ulong Counter);

internal sealed record VerifiedSequencerHead(SequencerHeadClaim Claim, Address Signer, Block Block);

internal sealed class SequencerHeadCodec
{
    internal const int MaxMessageSize = 10 * 1024 * 1024;
    internal const int MaxFrameSize = MaxMessageSize + 65536;
    private const int MinTransactionSize = 10;
    private const int MinHeaderSize = 496;
    private const int MinWithdrawalSize = 25;

    private static readonly Hash256 DomainType = Keccak.Compute("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
    private static readonly Hash256 DomainName = Keccak.Compute("EEZ Sequencer");
    private static readonly Hash256 DomainVersion = Keccak.Compute("0");
    private static readonly Hash256 ClaimType = Keccak.Compute("SignedHead(uint64 rollupId,uint64 number,bytes32 blockHash,bytes32 parentHash,uint64 epochMs,uint64 counter)");
    private static readonly UInt256 CurveOrder = UInt256.Parse("0xfffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141");
    private static readonly UInt256 HalfCurveOrder = CurveOrder >> 1;
    private static readonly RlpLimit MessageLimit = new(MaxMessageSize);
    private static readonly HeaderDecoder HeaderDecoder = new(null, MessageLimit);
    private static readonly WithdrawalDecoder WithdrawalDecoder = new();

    private readonly ulong _rollupId;
    private readonly Address _sequencer;
    private readonly byte[] _topic;

    internal SequencerHeadCodec(ulong chainId, Address registry, ulong rollupId, Address sequencer)
    {
        _rollupId = rollupId;
        _sequencer = sequencer;
        Topic = FormattableString.Invariant($"/eez/{chainId}/{rollupId}/heads/0");
        _topic = Encoding.ASCII.GetBytes(Topic);

        Span<byte> domain = stackalloc byte[5 * 32];
        domain.Clear();
        DomainType.Bytes.CopyTo(domain);
        DomainName.Bytes.CopyTo(domain[32..]);
        DomainVersion.Bytes.CopyTo(domain[64..]);
        BinaryPrimitives.WriteUInt64BigEndian(domain.Slice(120, 8), chainId);
        registry.Bytes.CopyTo(domain[140..]);
        DomainSeparator = Keccak.Compute(domain);
    }

    internal string Topic { get; }
    internal Hash256 DomainSeparator { get; }

    internal Hash256 SigningHash(in SequencerHeadClaim claim)
    {
        Span<byte> data = stackalloc byte[7 * 32];
        data.Clear();
        ClaimType.Bytes.CopyTo(data);
        BinaryPrimitives.WriteUInt64BigEndian(data.Slice(56, 8), claim.RollupId);
        BinaryPrimitives.WriteUInt64BigEndian(data.Slice(88, 8), claim.Number);
        claim.BlockHash.Bytes.CopyTo(data[96..]);
        claim.ParentHash.Bytes.CopyTo(data[128..]);
        BinaryPrimitives.WriteUInt64BigEndian(data.Slice(184, 8), claim.EpochMs);
        BinaryPrimitives.WriteUInt64BigEndian(data.Slice(216, 8), claim.Counter);

        Span<byte> typed = stackalloc byte[66];
        typed[0] = 0x19;
        typed[1] = 0x01;
        DomainSeparator.Bytes.CopyTo(typed[2..]);
        Keccak.Compute(data).Bytes.CopyTo(typed[34..]);
        return Keccak.Compute(typed);
    }

    internal bool TryDecode(ReadOnlySpan<byte> compressed, [NotNullWhen(true)] out VerifiedSequencerHead? head)
    {
        head = null;
        if (!HasValidPreamble(compressed)) return false;
        try
        {
            using IMemoryOwner<byte> decoded = Snappy.DecompressToMemory(compressed);
            RlpReader outer = new(decoded.Memory.Span);
            RlpReader reader = outer.EnterSequence(out int length);
            if (length != decoded.Memory.Length) return false;
            RlpReader fields = reader.EnterSequence(out int claimLength);
            SequencerHeadClaim claim = new(fields.DecodeULong(), fields.DecodeULong(), fields.DecodeKeccak(),
                fields.DecodeKeccak(), fields.DecodeULong(), fields.DecodeULong());
            reader.ExitSequence(in fields, claimLength);
            if (claim.RollupId != _rollupId) return false;
            ReadOnlySpan<byte> signature = reader.DecodeByteArraySpan(RlpLimit.L65);
            if (!VerifySignature(claim, signature)) return false;
            ReadOnlySpan<byte> blockBytes = reader.DecodeByteArraySpan(MessageLimit);
            outer.ExitSequence(in reader, length);
            outer.CheckEnd();
            if (blockBytes.IsEmpty) return false;
            ReadOnlyMemory<byte> blockMemory = decoded.Memory.SliceOf(blockBytes);
            if (!AuthenticateBody(blockMemory, claim, out BlockHeader? header)) return false;
            Block block = DecodeBlock(blockBytes, header);
            if (!ValidateBlock(block, blockBytes, claim)) return false;
            head = new(claim, _sequencer, block);
            return true;
        }
        catch (Exception error) when (error is RlpReaderException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    internal byte[] MessageId(ReadOnlySpan<byte> compressed)
    {
        if (HasValidPreamble(compressed))
        {
            try
            {
                using IMemoryOwner<byte> decoded = Snappy.DecompressToMemory(compressed);
                return HashMessage(decoded.Memory.Span, true);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                return HashMessage(compressed, false);
            }
        }
        return HashMessage(compressed, false);
    }

    private byte[] HashMessage(ReadOnlySpan<byte> data, bool validSnappy)
    {
        Span<byte> prefix = stackalloc byte[12];
        prefix.Clear();
        prefix[0] = validSnappy ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(prefix[4..], (ulong)_topic.Length);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(_topic);
        hash.AppendData(data);
        Span<byte> result = stackalloc byte[32];
        hash.GetHashAndReset(result);
        return result[..20].ToArray();
    }

    private bool VerifySignature(in SequencerHeadClaim claim, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 65 || signature[64] is not (27 or 28)) return false;
        UInt256 r = new(signature[..32], isBigEndian: true);
        UInt256 s = new(signature.Slice(32, 32), isBigEndian: true);
        if (r.IsZero || r >= CurveOrder || s.IsZero || s > HalfCurveOrder) return false;
        Span<byte> publicKey = stackalloc byte[65];
        return SecP256k1.RecoverKeyFromCompact(publicKey, SigningHash(claim).Bytes, signature[..64], signature[64] - 27, false)
            && PublicKey.ComputeAddress(publicKey[1..]) == _sequencer;
    }

    private static bool ValidateBlock(Block block, ReadOnlySpan<byte> bytes, in SequencerHeadClaim claim)
    {
        if (block.Number != claim.Number || block.ParentHash != claim.ParentHash || block.Hash != claim.BlockHash) return false;
        RlpReader outer = new(bytes);
        RlpReader reader = outer.EnterSequence(out int end);
        if (end != bytes.Length) return false;
        reader.SkipItem();
        ReadOnlySpan<byte> transactions = ReadItem(ref reader);
        ReadOnlySpan<byte> uncles = ReadItem(ref reader);
        RlpReader uncleList = new(uncles);
        RlpReader uncleReader = uncleList.EnterSequence(out int unclesLength);
        while (uncleReader.HasNext)
        {
            if (!ValidateHeaderShape(ReadItem(ref uncleReader))) return false;
        }
        uncleList.ExitSequence(in uncleReader, unclesLength);
        uncleList.CheckEnd();
        if (!IsCanonical(block.Transactions, transactions, TxDecoder.Instance)) return false;
        if (block.Withdrawals is { } withdrawals && !IsCanonical(withdrawals, ReadItem(ref reader), WithdrawalDecoder)) return false;
        outer.ExitSequence(in reader, end);
        outer.CheckEnd();
        foreach (Transaction transaction in block.Transactions)
        {
            if (transaction.Type == EezConstants.SystemTxType)
            {
                if (transaction.To != EezConstants.Eezl2Address) return false;
            }
            else if ((byte)transaction.Type > 4) return false;
        }
        RlpReader transactionList = new(transactions);
        RlpReader transactionReader = transactionList.EnterSequence(out int transactionLength);
        foreach (Transaction transaction in block.Transactions)
        {
            ReadOnlySpan<byte> raw = ReadItem(ref transactionReader);
            if (transaction.Type != TxType.Legacy)
            {
                RlpReader wrapped = new(raw);
                raw = wrapped.DecodeByteArraySpan(MessageLimit);
            }
            transaction.Hash = Keccak.Compute(raw);
        }
        transactionList.ExitSequence(in transactionReader, transactionLength);
        transactionList.CheckEnd();
        return true;
    }

    private static bool AuthenticateBody(ReadOnlyMemory<byte> bytes, in SequencerHeadClaim claim,
        [NotNullWhen(true)] out BlockHeader? header)
    {
        header = null;
        RlpReader outer = new(bytes.Span);
        RlpReader reader = outer.EnterSequence(out int end);
        if (end != bytes.Length) return false;
        ReadOnlySpan<byte> headerBytes = ReadItem(ref reader);
        if (!ValidateHeaderShape(headerBytes) || Keccak.Compute(headerBytes) != claim.BlockHash) return false;
        BlockHeader decoded = HeaderDecoder.DecodeCompleteNotNull(headerBytes);
        if (decoded.Number != claim.Number || decoded.ParentHash != claim.ParentHash) return false;
        ReadOnlyMemory<byte> transactions = ReadItemMemory(ref reader, bytes);
        ReadOnlyMemory<byte> uncles = ReadItemMemory(ref reader, bytes);
        if (Keccak.Compute(uncles.Span) != decoded.UnclesHash || !MatchesRoot(transactions, decoded.TxRoot!, true)) return false;
        if (reader.HasNext)
        {
            if (decoded.WithdrawalsRoot is null || !MatchesRoot(ReadItemMemory(ref reader, bytes), decoded.WithdrawalsRoot, false)) return false;
        }
        else if (decoded.WithdrawalsRoot is not null) return false;
        outer.ExitSequence(in reader, end);
        outer.CheckEnd();
        header = decoded;
        return true;
    }

    private static bool MatchesRoot(ReadOnlyMemory<byte> bytes, Hash256 expected, bool transactions)
    {
        RlpReader outer = new(bytes.Span);
        RlpReader reader = outer.EnterSequence(out int end);
        if (end != bytes.Length) return false;
        if (!reader.HasNext) return expected == Keccak.EmptyTreeHash;
        if (expected == Keccak.EmptyTreeHash) return false;
        int maximum = bytes.Length / (transactions ? MinTransactionSize : MinWithdrawalSize) + 1;
        int count = reader.CountItems(maximum);
        using ArrayPoolList<ReadOnlyMemory<byte>> values = new(count, count);
        for (int index = 0; index < count; index++)
        {
            ReadOnlyMemory<byte> item = ReadItemMemory(ref reader, bytes);
            if (transactions && item.Span[0] < 0xc0)
            {
                RlpReader wrapped = new(item.Span);
                ReadOnlySpan<byte> raw = wrapped.DecodeByteArraySpan(MessageLimit);
                wrapped.CheckEnd();
                if (raw.IsEmpty) return false;
                item = item.Slice(wrapped.BytesRead - raw.Length, raw.Length);
            }
            if (item.Length < (transactions ? MinTransactionSize : MinWithdrawalSize)) return false;
            values[index] = item;
        }
        outer.ExitSequence(in reader, end);
        outer.CheckEnd();
        return TxTrie.CalculateRoot(values.AsSpan()) == expected;
    }

    private static ReadOnlyMemory<byte> ReadItemMemory(ref RlpReader reader, ReadOnlyMemory<byte> bytes)
    {
        int start = bytes.Length - reader.Remaining;
        ReadOnlySpan<byte> item = reader.ReadEncodedItem();
        return bytes.Slice(start, item.Length);
    }

    private static Block DecodeBlock(ReadOnlySpan<byte> bytes, BlockHeader header)
    {
        RlpReader outer = new(bytes);
        RlpReader reader = outer.EnterSequence(out int end);
        if (end != bytes.Length) throw new RlpReaderException("Signed block has trailing bytes.");
        reader.SkipItem();
        Transaction[] transactions = TxDecoder.Instance.DecodeNonNullArray(ref reader, RlpBehaviors.SkipPooledTransactions | RlpBehaviors.ExcludeHashes,
            new RlpLimit(bytes.Length / MinTransactionSize + 1));
        BlockHeader[] uncles = reader.DecodeNonNullArray(HeaderDecoder, limit: new RlpLimit(bytes.Length / MinHeaderSize + 1));
        Withdrawal[]? withdrawals = reader.HasNext
            ? reader.DecodeNonNullArray(WithdrawalDecoder, limit: new RlpLimit(bytes.Length / MinWithdrawalSize + 1)) : null;
        outer.ExitSequence(in reader, end);
        outer.CheckEnd();
        return new Block(header, new BlockBody(transactions, uncles, withdrawals)) { EncodedSize = bytes.Length };
    }

    private static ReadOnlySpan<byte> ReadItem(ref RlpReader reader) => reader.ReadEncodedItem();

    private static bool ValidateHeaderShape(ReadOnlySpan<byte> bytes)
    {
        RlpReader outer = new(bytes);
        RlpReader reader = outer.EnterSequence(out int end);
        if (end != bytes.Length) return false;
        int fields = 0;
        while (reader.HasNext && fields < 23)
        {
            if (fields == 7) reader.DecodeUInt256();
            else if (fields is 8 or 9 or 10 or 11 or 15 or 17 or 18 or 22) reader.DecodeULong();
            else
            {
                ReadOnlySpan<byte> value = reader.DecodeByteArraySpan(MessageLimit);
                int size = fields switch { 2 => 20, 6 => 256, 12 => value.Length, 14 => 8, _ => 32 };
                if (value.Length != size) return false;
            }
            fields++;
        }
        return fields >= 15 && !reader.HasNext && end == bytes.Length;
    }

    private static bool IsCanonical<T>(T[] items, ReadOnlySpan<byte> bytes, IRlpDecoder<T> decoder) where T : class
    {
        int length = decoder.GetLength(items);
        if (length != bytes.Length) return false;
        using ArrayPoolSpan<byte> encoded = new(length);
        RlpWriter writer = RlpWriter.ContentWriter(encoded);
        decoder.Encode(ref writer, items);
        return bytes.SequenceEqual(encoded);
    }

    internal static bool HasValidPreamble(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxFrameSize) return false;
        uint length = 0;
        for (int index = 0; index < Math.Min(4, data.Length); index++)
        {
            byte next = data[index];
            length |= (uint)(next & 0x7f) << (index * 7);
            if (next < 0x80)
                return (index == 0 || next != 0) && length <= MaxMessageSize;
        }
        return false;
    }

}
