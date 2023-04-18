// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Text;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

[DebuggerDisplay("{Hash} ({Number})")]
public class BlockHeader
{
    internal BlockHeader() { }

    public BlockHeader(
        Keccak parentHash,
        Keccak unclesHash,
        Address beneficiary,
        in UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[] extraData,
        UInt256? excessDataGas = null)
    {
        ParentHash = parentHash;
        UnclesHash = unclesHash;
        Beneficiary = beneficiary;
        Difficulty = difficulty;
        Number = number;
        GasLimit = gasLimit;
        Timestamp = timestamp;
        ExtraData = extraData;
        ExcessDataGas = excessDataGas;
    }

    public WeakReference<BlockHeader>? MaybeParent { get; set; }
    public bool IsGenesis => Number == 0L;
    public Keccak? ParentHash { get; set; }
    public Keccak? UnclesHash { get; set; }
    public Address? Author { get; set; }
    public Address? Beneficiary { get; set; }
    public Address? GasBeneficiary => Author ?? Beneficiary;
    public Keccak? StateRoot { get; set; }
    public Keccak? TxRoot { get; set; }
    public Keccak? ReceiptsRoot { get; set; }
    public Bloom? Bloom { get; set; }
    public UInt256 Difficulty { get; set; }
    public long Number { get; set; }
    public long GasUsed { get; set; }
    public long GasLimit { get; set; }
    public ulong Timestamp { get; set; }
    public DateTime TimestampDate => DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).LocalDateTime;
    public byte[] ExtraData { get; set; } = Array.Empty<byte>();
    public Keccak? MixHash { get; set; }
    public Keccak? Random => MixHash;
    public ulong Nonce { get; set; }
    public Keccak? Hash { get; set; }
    public UInt256? TotalDifficulty { get; set; }
    public byte[]? AuRaSignature { get; set; }
    public long? AuRaStep { get; set; }
    public UInt256 BaseFeePerGas { get; set; }
    public Keccak? WithdrawalsRoot { get; set; }
    public UInt256? ExcessDataGas { get; set; }

    public bool HasBody => (TxRoot is not null && TxRoot != Keccak.EmptyTreeHash)
        || (UnclesHash is not null && UnclesHash != Keccak.OfAnEmptySequenceRlp)
        || (WithdrawalsRoot is not null && WithdrawalsRoot != Keccak.EmptyTreeHash);

    public bool HasTransactions => (TxRoot is not null && TxRoot != Keccak.EmptyTreeHash);

    public string SealEngineType { get; set; } = Core.SealEngineType.Ethash;
    public bool IsPostMerge { get; set; }

    public string ToString(string indent)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{indent}Hash: {Hash}");
        builder.AppendLine($"{indent}Number: {Number}");
        builder.AppendLine($"{indent}Parent: {ParentHash}");
        builder.AppendLine($"{indent}Beneficiary: {Beneficiary}");
        builder.AppendLine($"{indent}Gas Limit: {GasLimit}");
        builder.AppendLine($"{indent}Gas Used: {GasUsed}");
        builder.AppendLine($"{indent}Timestamp: {Timestamp}");
        builder.AppendLine($"{indent}Extra Data: {ExtraData.ToHexString()}");
        builder.AppendLine($"{indent}Difficulty: {Difficulty}");
        builder.AppendLine($"{indent}Mix Hash: {MixHash}");
        builder.AppendLine($"{indent}Nonce: {Nonce}");
        builder.AppendLine($"{indent}Uncles Hash: {UnclesHash}");
        builder.AppendLine($"{indent}Tx Root: {TxRoot}");
        builder.AppendLine($"{indent}Receipts Root: {ReceiptsRoot}");
        builder.AppendLine($"{indent}State Root: {StateRoot}");
        builder.AppendLine($"{indent}BaseFeePerGas: {BaseFeePerGas}");
        if (WithdrawalsRoot is not null)
        {
            builder.AppendLine($"{indent}WithdrawalsRoot: {WithdrawalsRoot}");
        }
        if (ExcessDataGas is not null)
        {
            builder.AppendLine($"{indent}ExcessDataGas: {ExcessDataGas}");
        }
        builder.AppendLine($"{indent}IsPostMerge: {IsPostMerge}");
        builder.AppendLine($"{indent}TotalDifficulty: {TotalDifficulty}");

        return builder.ToString();
    }

    public override string ToString() => ToString(string.Empty);

    public string ToString(Format format) => format switch
    {
        Format.Full => ToString(string.Empty),
        Format.FullHashAndNumber => Hash is null ? $"{Number} null" : $"{Number} ({Hash})",
        _ => Hash is null ? $"{Number} null" : $"{Number} ({Hash.ToShortString()})",
    };

    [Todo(Improve.Refactor, "Use IFormattable here")]
    public enum Format
    {
        Full,
        Short,
        FullHashAndNumber
    }

    public BlockHeader Clone()
    {
        BlockHeader header = (BlockHeader)MemberwiseClone();
        header.Bloom = Bloom?.Clone() ?? new Bloom();
        return header;
    }
}
