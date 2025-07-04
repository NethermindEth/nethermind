// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.Derivation;

public static class DepositEvent
{
    public static readonly string ABI = "TransactionDeposited(address,address,uint256,bytes)";
    public static readonly Hash256 ABIHash = Keccak.Compute(ABI);
    public static readonly Hash256 Version0 = Hash256.Zero;

    public enum SourceDomain : ulong
    {
        User = 0,
        L1Info = 1,
        Upgrade = 2,
        AfterForceInclude = 3,
    }
}

public class DepositTransactionBuilder(ulong chainId, CLChainSpecEngineParameters engineParameters)
{
    private const int SystemTxDataLengthEcotone = 164;

    public Transaction BuildL1InfoTransaction(L1BlockInfo blockInfo)
    {
        byte[] data = new byte[SystemTxDataLengthEcotone];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(), L1BlockInfoBuilder.L1InfoTransactionMethodId);
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

        source.Clear();
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

    public IEnumerable<Transaction> BuildUserDepositTransactions(ReceiptForRpc[] receipts)
    {
        List<Transaction> result = [];
        foreach (var receipt in receipts)
        {
            if (receipt.Status != StatusCode.Success) continue;
            foreach (var log in receipt.Logs)
            {
                if (log.Address != engineParameters.OptimismPortalProxy) continue;
                if (log.Topics.Length == 0 || log.Topics[0] != DepositEvent.ABIHash) continue;

                try
                {
                    Transaction tx = DecodeDepositTransactionFromLogEvent(log);
                    result.Add(tx);
                }
                catch (Exception e)
                {
                    throw new ArgumentException($"Failed to decode {nameof(Transaction)} from {nameof(LogEntryForRpc)}", e);
                }
            }
        }
        return result;
    }

    /*
    See: https://github.com/ethereum-optimism/optimism/blob/ca4b1f687977d3f771d6e3a1b8c6f113f2331f63/packages/contracts-bedrock/src/L1/OptimismPortal2.sol#L144

    /// @notice Emitted when a transaction is deposited from L1 to L2.
    ///         The parameters of this event are read by the rollup node and used to derive deposit
    ///         transactions on L2.
    /// @param from       Address that triggered the deposit transaction.
    /// @param to         Address that the deposit transaction is directed to.
    /// @param version    Version of this deposit transaction event.
    /// @param opaqueData ABI encoded deposit data to be parsed off-chain.
    event TransactionDeposited(address indexed from, address indexed to, uint256 indexed version, bytes opaqueData);
    */
    private static Transaction DecodeDepositTransactionFromLogEvent(LogEntryForRpc log)
    {
        if (log.Topics.Length != 4) throw new ArgumentException($"Expected 4 event topics (address indexed from, address indexed to, uint256 indexed version, bytes opaqueData), got {log.Topics.Length}");
        if (log.Topics[1].Bytes.Length != 32) throw new ArgumentException($"Expected padded {nameof(Address)}, got {log.Topics[1]}");
        if (log.Topics[2].Bytes.Length != 32) throw new ArgumentException($"Expected padded {nameof(Address)}, got {log.Topics[2]}");

        var from = new Address(log.Topics[1].Bytes[^Address.Size..]);
        var to = new Address(log.Topics[2].Bytes[^Address.Size..]);

        static Hash256 ComputeSourceHash(Hash256 l1BlockHash, ulong logIndex)
        {
            Span<byte> buffer = stackalloc byte[32 * 2];
            Span<byte> span = buffer;
            l1BlockHash.Bytes.CopyTo(span.TakeAndMove(Hash256.Size));
            span.TakeAndMove(24); // skip 24 bytes
            BinaryPrimitives.WriteUInt64BigEndian(span.TakeAndMove(8), logIndex);
            var depositIdHash = Keccak.Compute(buffer);

            buffer.Clear();
            span = buffer;

            span.TakeAndMove(24); // skip 24 bytes
            BinaryPrimitives.WriteUInt64BigEndian(span.TakeAndMove(8), (ulong)DepositEvent.SourceDomain.User);
            depositIdHash.Bytes.CopyTo(span.TakeAndMove(Hash256.Size));

            return Keccak.Compute(buffer);
        }

        var version = log.Topics[3];
        if (version == DepositEvent.Version0)
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

        throw new ArgumentException($"Unknown log event version: {version}");
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
        int opaqueDataLength = 32 + 32 + 8 + 1 + Data.Length;
        var opaqueData = ArrayPool<byte>.Shared.Rent(opaqueDataLength);
        {
            Span<byte> span = opaqueData.AsSpan(0, opaqueDataLength);

            Mint.ToBigEndian(span.TakeAndMove(32));
            Value.ToBigEndian(span.TakeAndMove(32));
            BinaryPrimitives.WriteUInt64BigEndian(span.TakeAndMove(8), Gas);
            span.TakeAndMove(1)[0] = (byte)(IsCreation ? 1 : 0);
            Data.CopyTo(span.TakeAndMove(Data.Length));
        }
        var result = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, Signature, opaqueData[..opaqueDataLength]);
        ArrayPool<byte>.Shared.Return(opaqueData);
        return result;
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
