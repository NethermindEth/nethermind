// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.Derivation;

public class DepositTransactionBuilder(ulong chainId, CLChainSpecEngineParameters engineParameters)
{
    private const int SystemTxDataLengthEcotone = 164;
    private static readonly string DepositEventABI = "TransactionDeposited(address,address,uint256,bytes)";
    private static readonly Hash256 DepositEventABIHash = Keccak.Compute(DepositEventABI);
    private static readonly Hash256 DepositEventVersion0 = Hash256.Zero;

    public Transaction BuildL1InfoTransaction(L1BlockInfo blockInfo)
    {
        byte[] data = new byte[SystemTxDataLengthEcotone];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(), 1141530144); // TODO method id
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), blockInfo.BaseFeeScalar);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), blockInfo.BlobBaseFeeScalar);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(12), blockInfo.SequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(20), blockInfo.Timestamp);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(28), blockInfo.Number);
        blockInfo.BaseFee.ToBigEndian().CopyTo(data, 36);
        blockInfo.BlobBaseFee.ToBigEndian().CopyTo(data, 68);
        blockInfo.BlockHash.Bytes.CopyTo(data.AsSpan(100));
        blockInfo.BatcherAddress.Bytes.CopyTo(data, 144);

        Span<byte> source = stackalloc byte[64];
        blockInfo.BlockHash.Bytes.CopyTo(source);
        BinaryPrimitives.WriteUInt64BigEndian(source[56..], blockInfo.SequenceNumber);
        Hash256 depositInfoHash = Keccak.Compute(source);

        source.Fill(0);
        BinaryPrimitives.WriteUInt64BigEndian(source[24..32], 1);
        depositInfoHash.Bytes.CopyTo(source[32..]);
        Hash256 sourceHash = Keccak.Compute(source);

        return new()
        {
            Type = TxType.DepositTx,
            Data = data,
            ChainId = chainId,
            SenderAddress = engineParameters.SystemTransactionSender,
            To = engineParameters.SystemTransactionTo,
            GasLimit = 1000000,
            IsOPSystemTransaction = false,
            Value = UInt256.Zero,
            SourceHash = sourceHash
        };
    }

    public List<Transaction> BuildUserDepositTransactions(Address depositAddress, List<ReceiptForRpc> receipts)
    {
        List<Transaction> result = [];

        foreach (var receipt in receipts)
        {
            if (receipt.Status != 1) { continue; }
            foreach (var log in receipt.Logs ?? [])
            {
                if (log.Address != depositAddress) { continue; }
                if (log.Topics.Length == 0 || log.Topics[0] != DepositEventABIHash) { continue; }

                Transaction tx = UnmarshalDepositTransactionFromLogEvent(log);
                result.Add(tx);
            }
        }

        return result;
    }

    private Transaction UnmarshalDepositTransactionFromLogEvent(LogEntryForRpc log)
    {
        if (log.Topics.Length != 4) throw new ArgumentException($"Expected 4 event topics (address indexed from, address indexed to, uint256 indexed version, bytes opaqueData), got {log.Topics.Length}");
        if (log.Topics[1].Bytes.Length != 32) { throw new ArgumentException($"Expected padded {nameof(Address)}, got {log.Topics[1]}"); }
        if (log.Topics[2].Bytes.Length != 32) { throw new ArgumentException($"Expected padded {nameof(Address)}, got {log.Topics[2]}"); }

        var from = new Address(log.Topics[1].Bytes[^Address.Size..]);
        var to = new Address(log.Topics[2].Bytes[^Address.Size..]);

        static Hash256 ComputeSourceHash(Hash256 l1BlockHash, ulong logIndex) {
            return Keccak.Zero;
        }

        var version = log.Topics[3];
        if (version == DepositEventVersion0)
        {
            var depositLogEventV0 = DepositLogEventV0.FromBytes(log.Data);
            var sourceHash = ComputeSourceHash(log.BlockHash, (ulong)(log.LogIndex ?? 0)); // TODO: Unsafe cast with possible null;

            return new()
            {
                Type = TxType.DepositTx,
                SenderAddress = from,
                To = depositLogEventV0.IsCreation ? null : to,
                Mint = depositLogEventV0.Mint,
                Value = depositLogEventV0.Value,
                GasLimit = (long)depositLogEventV0.Gas, // WARNING: dangerous cast
                Data = depositLogEventV0.Data.ToArray(),
                SourceHash = sourceHash,
                IsOPSystemTransaction = false,
            };
        }
        else
        {
            throw new FormatException($"Unknown log event version: {version}");
        }
    }

    public Transaction[] BuildUpgradeTransactions()
    {
        return []; // TODO implement
    }

    public Transaction[] BuildForceIncludeTransactions()
    {
        return []; // TODO implement
    }
}

public readonly ref struct DepositLogEventV0
{
    public UInt256 Mint { get; init; }
    public UInt256 Value { get; init; }
    public UInt64 Gas { get; init; }
    public bool IsCreation { get; init; }
    public ReadOnlySpan<byte> Data { get; init; }

    private static readonly AbiSignature Signature = new(string.Empty, AbiType.DynamicBytes);

    public byte[] ToBytes()
    {
        // NOTE: Format is as follows
        //      opaqueData   = [mint (32 bytes), value (32 bytes), gas (8 bytes), isCreation (1 byte), ...data]
        //      result       = [0x20 (32 bytes), opaqueData.Length (32 bytes), data (padded)]

        // TODO: We could even hand-roll the entire process using a single intermediate array (several copies here).

        // TODO: Can we `stackalloc`? What is `data.Length` max size?
        var opaqueData = new byte[32 + 32 + 8 + 1 + Data.Length];
        {
            Span<byte> span = opaqueData;

            Mint.ToBigEndian(span.TakeAndMove(32));
            Value.ToBigEndian(span.TakeAndMove(32));
            BinaryPrimitives.WriteUInt64BigEndian(span.TakeAndMove(8), Gas);
            span.TakeAndMove(1)[0] = (byte)(IsCreation ? 1 : 0);
            Data.CopyTo(span.TakeAndMove(Data.Length));
        }
        return AbiEncoder.Instance.Encode(AbiEncodingStyle.None, Signature, opaqueData);
    }

    public static DepositLogEventV0 FromBytes(byte[] source) // TODO: Add `ReadOnlySpan<byte>` overloads
    {
        var opaqueData = (byte[])AbiEncoder.Instance.Decode(AbiEncodingStyle.None, Signature, source)[0];
        Span<byte> span = opaqueData;

        return new()
        {
            Mint = new UInt256(span.TakeAndMove(32), isBigEndian: true),
            Value = new UInt256(span.TakeAndMove(32), isBigEndian: true),
            Gas = BinaryPrimitives.ReadUInt64BigEndian(span.TakeAndMove(8)),
            IsCreation = span.TakeAndMove(1)[0] == 1,
            Data = span,
        };
    }
}
