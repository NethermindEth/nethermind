// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL.P2P;

public class PayloadDecoder : IPayloadDecoder
{
    public static readonly PayloadDecoder Instance = new();

    private const int PrefixDataSize = 560;

    private PayloadDecoder()
    {
    }

    public ExecutionPayloadV3 DecodePayload(ReadOnlySpan<byte> data)
    {
        ExecutionPayloadV3 payload = new();

        if (PrefixDataSize >= data.Length)
        {
            throw new ArgumentException("Invalid payload data size");
        }

        ReadOnlySpan<byte> movingData = data;
        payload.ParentBeaconBlockRoot = new(movingData.TakeAndMove(32));
        payload.ParentHash = new(movingData.TakeAndMove(32));
        payload.FeeRecipient = new(movingData.TakeAndMove(20));
        payload.StateRoot = new(movingData.TakeAndMove(32));
        payload.ReceiptsRoot = new(movingData.TakeAndMove(32));
        payload.LogsBloom = new(movingData.TakeAndMove(256));
        payload.PrevRandao = new(movingData.TakeAndMove(32));
        payload.BlockNumber = (long)BinaryPrimitives.ReadUInt64LittleEndian(movingData.TakeAndMove(8));
        payload.GasLimit = (long)BinaryPrimitives.ReadUInt64LittleEndian(movingData.TakeAndMove(8));
        payload.GasUsed = (long)BinaryPrimitives.ReadUInt64LittleEndian(movingData.TakeAndMove(8));
        payload.Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(movingData.TakeAndMove(8));
        UInt32 extraDataOffset = 32 + BinaryPrimitives.ReadUInt32LittleEndian(movingData.TakeAndMove(4));
        payload.BaseFeePerGas = new(movingData.TakeAndMove(32));
        payload.BlockHash = new(movingData.TakeAndMove(32));
        UInt32 transactionsOffset = 32 + BinaryPrimitives.ReadUInt32LittleEndian(movingData.TakeAndMove(4));
        UInt32 withdrawalsOffset = 32 + BinaryPrimitives.ReadUInt32LittleEndian(movingData.TakeAndMove(4));
        payload.BlobGasUsed = BinaryPrimitives.ReadUInt64LittleEndian(movingData.TakeAndMove(8));
        payload.ExcessBlobGas = BinaryPrimitives.ReadUInt64LittleEndian(movingData.TakeAndMove(8));

        if (withdrawalsOffset > data.Length || transactionsOffset >= withdrawalsOffset || extraDataOffset > transactionsOffset || withdrawalsOffset != data.Length)
        {
            throw new ArgumentException($"Invalid offsets. Data length: {data.Length}, extraData: {extraDataOffset}, transactions: {transactionsOffset}, withdrawals: {withdrawalsOffset}");
        }

        payload.ExtraData = data[(int)extraDataOffset..(int)transactionsOffset].ToArray();
        payload.Transactions = DecodeTransactions(data[(int)transactionsOffset..(int)withdrawalsOffset]);
        payload.Withdrawals = [];

        return payload;
    }

    byte[][] DecodeTransactions(ReadOnlySpan<byte> data)
    {
        if (4 > data.Length) throw new ArgumentException("Invalid transaction data");
        UInt32 firstTxOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        UInt32 txCount = firstTxOffset / 4;
        byte[][] txs = new byte[txCount][];
        int previous = (int)firstTxOffset;
        for (int i = 0; i < txCount; i++)
        {
            int next;
            if (i + 1 < txCount)
            {
                if (i * 4 + 8 > data.Length) throw new ArgumentException("Invalid transaction data");
                next = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4 + 4)..(i * 4 + 8)]);
            }
            else
            {
                next = data.Length;
            }
            if (previous >= next || next > data.Length) throw new ArgumentException("Invalid transaction offset");
            txs[i] = data[previous..next].ToArray();
            previous = next;
        }

        return txs;
    }

    public byte[] EncodePayload(ExecutionPayloadV3 payload) => throw new NotImplementedException();
}
