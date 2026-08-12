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
        Hash256 parentHash,
        Hash256 unclesHash,
        Address beneficiary,
        in UInt256 difficulty,
        ulong number,
        ulong gasLimit,
        ulong timestamp,
        byte[] extraData,
        ulong? blobGasUsed = null,
        ulong? excessBlobGas = null,
        Hash256? parentBeaconBlockRoot = null,
        Hash256? requestsHash = null,
        ulong? slotNumber = null)
    {
        ParentHash = parentHash;
        UnclesHash = unclesHash;
        Beneficiary = beneficiary;
        Difficulty = difficulty;
        Number = number;
        GasLimit = gasLimit;
        Timestamp = timestamp;
        ExtraData = extraData;
        ParentBeaconBlockRoot = parentBeaconBlockRoot;
        RequestsHash = requestsHash;
        BlobGasUsed = blobGasUsed;
        ExcessBlobGas = excessBlobGas;
        SlotNumber = slotNumber;
    }

    public virtual ulong GenesisBlockNumber => 0;
    public bool IsGenesis => Number == GenesisBlockNumber;
    public Hash256? ParentHash { get; set; }
    public Hash256? UnclesHash { get; set; }
    public Address? Author { get; set; }
    public Address? Beneficiary { get; set; }
    public Address? GasBeneficiary => Author ?? Beneficiary;
    public Hash256? StateRoot { get; set; }
    public Hash256? TxRoot { get; set; }
    public Hash256? ReceiptsRoot { get; set; }
    public Bloom? Bloom { get; set; }
    public UInt256 Difficulty;
    public ulong Number { get; set; }
    public ulong GasUsed { get; set; }
    public ulong GasLimit { get; set; }
    public ulong Timestamp { get; set; }
    public DateTime TimestampDate => DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).LocalDateTime;
    public byte[] ExtraData { get; set; } = [];
    public Hash256? MixHash { get; set; }
    public Hash256? Random => MixHash;
    public ulong Nonce { get; set; }
    public Hash256? Hash { get; set; }
    public UInt256? TotalDifficulty { get; set; }
    public UInt256 BaseFeePerGas;
    public Hash256? WithdrawalsRoot { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
    public Hash256? RequestsHash { get; set; }
    public Hash256? BlockAccessListHash { get; set; }
    public ulong? BlobGasUsed { get; set; }
    public ulong? ExcessBlobGas { get; set; }
    public ulong? SlotNumber { get; set; }

    /// <summary>EIP-8288 recursive STARK aggregating the block's transaction dependencies.</summary>
    public RecursiveStark? RecursiveStark { get; set; }
    public bool HasBody => (TxRoot is not null && TxRoot != Keccak.EmptyTreeHash)
                           || (UnclesHash is not null && UnclesHash != Keccak.OfAnEmptySequenceRlp)
                           || (WithdrawalsRoot is not null && WithdrawalsRoot != Keccak.EmptyTreeHash)
                           || (BlockAccessListHash is not null && BlockAccessListHash != Keccak.OfAnEmptySequenceRlp);

    public bool HasTransactions => TxRoot is not null && TxRoot != Keccak.EmptyTreeHash;

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
        if (ParentBeaconBlockRoot is not null)
        {
            builder.AppendLine($"{indent}ParentBeaconBlockRoot: {ParentBeaconBlockRoot}");
        }
        if (BlobGasUsed is not null || ExcessBlobGas is not null)
        {
            builder.AppendLine($"{indent}BlobGasUsed: {BlobGasUsed}");
            builder.AppendLine($"{indent}ExcessBlobGas: {ExcessBlobGas}");
        }
        builder.AppendLine($"{indent}IsPostMerge: {IsPostMerge}");
        builder.AppendLine($"{indent}TotalDifficulty: {TotalDifficulty}");
        if (RequestsHash is not null)
        {
            builder.AppendLine($"{indent}RequestsHash: {RequestsHash}");
        }
        if (BlockAccessListHash is not null)
        {
            builder.AppendLine($"{indent}BlockAccessListHash: {BlockAccessListHash}");
        }
        if (SlotNumber is not null)
        {
            builder.AppendLine($"{indent}SlotNumber: {SlotNumber}");
        }
        if (RecursiveStark is not null)
        {
            builder.AppendLine($"{indent}BlockDepsHash: {RecursiveStark.BlockDepsHash}");
        }

        return builder.ToString();
    }

    public override string ToString() => ToString(string.Empty);

    public string ToString(Format format) => format switch
    {
        Format.Full => ToString(string.Empty),
        Format.FullHashAndNumber => Hash is null ? $"{Number} null" : $"{Number} ({Hash})",
        _ => Hash is null ? $"{Number} null" : $"{Number} ({Hash.ToShortString()})",
    };

    /// <summary>
    /// Creates the child header used for simulated execution.
    /// </summary>
    /// <param name="timestamp">Timestamp assigned to the simulated child header.</param>
    /// <returns>A simulated child header with explicit default execution fields.</returns>
    public virtual BlockHeader CreateSimulatedChild(ulong timestamp)
    {
        Hash256? requestsHash = RequestsHash;
        return new BlockHeader(
            Hash!,
            Keccak.OfAnEmptySequenceRlp,
            Beneficiary!,
            UInt256.Zero,
            Number + 1,
            GasLimit,
            timestamp,
            [],
            requestsHash: requestsHash)
        {
            MixHash = Hash256.Zero,
        };
    }

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

    /// <summary>
    /// Copy carrying the consensus inputs needed to re-execute the block; execution outputs
    /// (state root, gas used, logs bloom) are reset so processing recomputes them. Subclasses
    /// override to also preserve subclass-specific seal fields (e.g. AuRa step + signature).
    /// </summary>
    public virtual BlockHeader CloneForProcessing()
    {
        BlockHeader clone = new(ParentHash!, UnclesHash!, Beneficiary!, Difficulty, Number, GasLimit, Timestamp, ExtraData);
        CopyProcessingFields(clone);
        return clone;
    }

    protected void CopyProcessingFields(BlockHeader dst)
    {
        dst.Bloom = Core.Bloom.Empty;
        dst.Author = Author;
        dst.Hash = Hash;
        dst.MixHash = MixHash;
        dst.Nonce = Nonce;
        dst.TxRoot = TxRoot;
        dst.TotalDifficulty = TotalDifficulty;
        dst.ReceiptsRoot = ReceiptsRoot;
        dst.BaseFeePerGas = BaseFeePerGas;
        dst.WithdrawalsRoot = WithdrawalsRoot;
        dst.RequestsHash = RequestsHash;
        dst.IsPostMerge = IsPostMerge;
        dst.ParentBeaconBlockRoot = ParentBeaconBlockRoot;
        dst.SlotNumber = SlotNumber;
        dst.BlockAccessListHash = BlockAccessListHash;
        dst.RecursiveStark = RecursiveStark;
        dst.BlobGasUsed = BlobGasUsed;
        dst.ExcessBlobGas = ExcessBlobGas;
    }
}
