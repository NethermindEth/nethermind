// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;

[DebuggerDisplay("{Hash} ({Number})")]
public class Block
{
    public Block(BlockHeader header, BlockBody body)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Body = body ?? throw new ArgumentNullException(nameof(body));
    }

    public Block(
        BlockHeader header,
        IEnumerable<Transaction> transactions,
        IEnumerable<BlockHeader> uncles,
        IEnumerable<Withdrawal>? withdrawals = null)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Body = new(transactions.ToArray(), uncles.ToArray(), withdrawals?.ToArray());
    }

    public Block(BlockHeader header) : this(
        header,
        new(null, null, header.WithdrawalsRoot is null ? null : Array.Empty<Withdrawal>())
    )
    { }

    public Block WithReplacedHeader(BlockHeader newHeader) => new(newHeader, Body);

    public Block WithReplacedBody(BlockBody newBody) => new(Header, newBody);

    public Block WithReplacedBodyCloned(BlockBody newBody) => new(Header.Clone(), newBody);

    public BlockHeader Header { get; }

    public BlockBody Body { get; }

    public bool IsGenesis => Header.IsGenesis;

    public Transaction[] Transactions
    {
        get => Body.Transactions;
        protected set => Body.Transactions = value; // needed to produce blocks with unknown transaction count on start
    }

    public BlockHeader[] Uncles => Body.Uncles; // do not add setter here

    public Withdrawal[]? Withdrawals => Body.Withdrawals;

    public Keccak? Hash => Header.Hash; // do not add setter here

    public Keccak? ParentHash => Header.ParentHash; // do not add setter here

    public ulong Nonce => Header.Nonce; // do not add setter here

    public Keccak? MixHash => Header.MixHash; // do not add setter here

    public byte[]? ExtraData => Header.ExtraData; // do not add setter here

    public Bloom? Bloom => Header.Bloom; // do not add setter here

    public Keccak? UnclesHash => Header.UnclesHash; // do not add setter here

    public Address? Beneficiary => Header.Beneficiary; // do not add setter here

    public Address? Author => Header.Author; // do not add setter here

    public Keccak? StateRoot => Header.StateRoot; // do not add setter here

    public Keccak? TxRoot => Header.TxRoot; // do not add setter here

    public Keccak? ReceiptsRoot => Header.ReceiptsRoot; // do not add setter here

    public long GasLimit => Header.GasLimit; // do not add setter here

    public long GasUsed => Header.GasUsed; // do not add setter here

    public ulong Timestamp => Header.Timestamp; // do not add setter here

    public DateTime TimestampDate => Header.TimestampDate; // do not add setter here

    public long Number => Header.Number; // do not add setter here

    public UInt256 Difficulty => Header.Difficulty; // do not add setter here

    public UInt256? TotalDifficulty => Header.TotalDifficulty; // do not add setter here

    public UInt256 BaseFeePerGas => Header.BaseFeePerGas; // do not add setter here

    public ulong? DataGasUsed => Header.DataGasUsed; // do not add setter here

    public ulong? ExcessDataGas => Header.ExcessDataGas; // do not add setter here

    public bool IsPostMerge => Header.IsPostMerge; // do not add setter here

    public bool IsBodyMissing => Header.HasBody && Body.IsEmpty;

    public Keccak? WithdrawalsRoot => Header.WithdrawalsRoot; // do not add setter here

    public override string ToString() => ToString(Format.Short);

    public string ToString(Format format) => format switch
    {
        Format.Full => ToFullString(),
        Format.FullHashAndNumber => Hash is null ? $"{Number} null" : $"{Number} ({Hash})",
        Format.HashNumberAndTx => Hash is null
            ? $"{Number} null, tx count: {Body.Transactions.Length}"
            : $"{Number} {TimestampDate:HH:mm:ss} ({Hash?.ToShortString()}), tx count: {Body.Transactions.Length}",
        Format.HashNumberDiffAndTx => Hash is null
            ? $"{Number} null, diff: {Difficulty}, tx count: {Body.Transactions.Length}"
            : $"{Number} ({Hash?.ToShortString()}), diff: {Difficulty}, tx count: {Body.Transactions.Length}",
        _ => Hash is null ? $"{Number} null" : $"{Number} ({Hash?.ToShortString()})"
    };

    private string ToFullString()
    {
        StringBuilder builder = new();
        builder.AppendLine($"Block {Number}");
        builder.AppendLine("  Header:");
        builder.Append(Header.ToString("    "));

        builder.AppendLine("  Uncles:");
        foreach (BlockHeader uncle in Body.Uncles ?? Array.Empty<BlockHeader>())
        {
            builder.Append(uncle.ToString("    "));
        }

        builder.AppendLine("  Transactions:");
        foreach (Transaction tx in Body?.Transactions ?? Array.Empty<Transaction>())
        {
            builder.Append(tx.ToString("    "));
        }

        builder.AppendLine("  Withdrawals:");

        foreach (var w in Body?.Withdrawals ?? Array.Empty<Withdrawal>())
        {
            builder.Append(w.ToString("    "));
        }

        return builder.ToString();
    }

    public enum Format
    {
        Full,
        FullHashAndNumber,
        HashNumberAndTx,
        HashNumberDiffAndTx,
        Short
    }
}
