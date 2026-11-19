// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Arkiv.Native;

/// <summary>One transaction's outcome, as Arkiv's executor produced its receipt.</summary>
public sealed record ArkivReceipt(bool Success, ulong CumulativeGasUsed, LogEntry[] Logs);

/// <summary>An account after a block, read from the committed GolemDB store.</summary>
public readonly record struct ArkivAccount(Address Address, UInt256 Balance, ulong Nonce);

/// <summary>An entity after a block: its <c>arkiv_getEntity</c> JSON, or null once it is gone.</summary>
public readonly record struct ArkivEntitySnapshot(Hash256 Key, byte[]? Json);

/// <summary>
/// Everything Nethermind takes from one block executed natively: the GolemDB state root, the receipts, the accounts
/// it wrote and the entities it touched. Decodes <c>arkiv_ffi::wire::BlockResult::encode</c>; the encoding is also
/// what is cached per block, so a block re-processed along the committed chain replays it exactly.
/// </summary>
public sealed class ArkivBlockResult
{
    public required Hash256 StateRoot { get; init; }
    public required ulong GasUsed { get; init; }
    public required bool RequestsEmpty { get; init; }
    public required ArkivReceipt[] Receipts { get; init; }
    public required ArkivAccount[] Accounts { get; init; }
    public required ArkivEntitySnapshot[] Entities { get; init; }

    public static ArkivBlockResult Decode(ReadOnlySpan<byte> bytes)
    {
        Reader reader = new(bytes);
        Hash256 stateRoot = new(reader.Bytes(32));
        ulong gasUsed = reader.U64();
        bool requestsEmpty = reader.U8() != 0;

        ArkivReceipt[] receipts = new ArkivReceipt[reader.U32()];
        for (int i = 0; i < receipts.Length; i++) receipts[i] = ReadReceipt(ref reader);

        ArkivAccount[] accounts = new ArkivAccount[reader.U32()];
        for (int i = 0; i < accounts.Length; i++)
        {
            Address address = new(reader.Bytes(20));
            UInt256 balance = new(reader.Bytes(32), isBigEndian: true);
            accounts[i] = new ArkivAccount(address, balance, reader.U64());
        }

        ArkivEntitySnapshot[] entities = new ArkivEntitySnapshot[reader.U32()];
        for (int i = 0; i < entities.Length; i++)
        {
            Hash256 key = new(reader.Bytes(32));
            int length = reader.I32();
            entities[i] = new ArkivEntitySnapshot(key, length < 0 ? null : reader.Bytes(length).ToArray());
        }

        if (!reader.AtEnd) throw new FormatException("trailing bytes after an Arkiv block result");
        return new ArkivBlockResult
        {
            StateRoot = stateRoot,
            GasUsed = gasUsed,
            RequestsEmpty = requestsEmpty,
            Receipts = receipts,
            Accounts = accounts,
            Entities = entities,
        };
    }

    /// <summary>One receipt as <c>arkiv_build_apply</c> returns it.</summary>
    public static ArkivReceipt DecodeReceipt(ReadOnlySpan<byte> bytes)
    {
        Reader reader = new(bytes);
        ArkivReceipt receipt = ReadReceipt(ref reader);
        if (!reader.AtEnd) throw new FormatException("trailing bytes after an Arkiv receipt");
        return receipt;
    }

    private static ArkivReceipt ReadReceipt(ref Reader reader)
    {
        bool success = reader.U8() != 0;
        ulong cumulative = reader.U64();
        LogEntry[] logs = new LogEntry[reader.U32()];
        for (int j = 0; j < logs.Length; j++)
        {
            Address address = new(reader.Bytes(20));
            Hash256[] topics = new Hash256[reader.U32()];
            for (int t = 0; t < topics.Length; t++) topics[t] = new Hash256(reader.Bytes(32));
            byte[] data = reader.Bytes((int)reader.U32()).ToArray();
            logs[j] = new LogEntry(address, data, topics);
        }
        return new ArkivReceipt(success, cumulative, logs);
    }

    /// <summary>The genesis block: GolemDB's genesis root, and nothing executed.</summary>
    public static ArkivBlockResult Genesis(Hash256 root) => new()
    {
        StateRoot = root,
        GasUsed = 0,
        RequestsEmpty = true,
        Receipts = [],
        Accounts = [],
        Entities = [],
    };

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _rest = bytes;

        public readonly bool AtEnd => _rest.IsEmpty;

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count > _rest.Length) throw new FormatException("truncated Arkiv block result");
            ReadOnlySpan<byte> taken = _rest[..count];
            _rest = _rest[count..];
            return taken;
        }

        public byte U8() => Bytes(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
    }
}
