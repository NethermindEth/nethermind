// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

public class PayloadDecoder : IPayloadDecoder
{
    public ExecutionPayloadV3 DecodePayload(byte[] data)
    {
        ExecutionPayloadV3 payload = new();

        // TODO: Add sanity checks
        int offset = 0;
        payload.ParentBeaconBlockRoot = new(data[offset..(offset + 32)]);
        offset += 32;
        payload.ParentHash = new(data[offset..(offset + 32)]);
        offset += 32;
        payload.FeeRecipient = new(data[offset..(offset + 20)]);
        offset += 20;
        payload.StateRoot = new(data[offset..(offset + 32)]);
        offset += 32;
        payload.ReceiptsRoot = new(data[offset..(offset + 32)]);
        offset += 32;
        payload.LogsBloom = new(data[offset..(offset + 256)]);
        offset += 256;
        payload.PrevRandao = new(data[offset..(offset + 32)]);
        offset += 32;
        payload.BlockNumber = (long)BitConverter.ToUInt64(data[offset..(offset + 8)]);
        offset += 8;
        payload.GasLimit = (long)BitConverter.ToUInt64(data[offset..(offset + 8)]);
        offset += 8;
        payload.GasUsed = (long)BitConverter.ToUInt64(data[offset..(offset + 8)]);
        offset += 8;
        payload.Timestamp = BitConverter.ToUInt64(data[offset..(offset + 8)]);
        offset += 8;

        UInt32 extraDataOffset = 32 + BitConverter.ToUInt32(data[offset..(offset + 4)]);
        offset += 4;
        payload.BaseFeePerGas = new(data[offset..(offset + 32)]);
        offset += 32;
        payload.BlockHash = new(data[offset..(offset + 32)]);
        offset += 32;

        UInt32 transactionsOffset = 32 + BitConverter.ToUInt32(data[offset..(offset + 4)]);
        offset += 4;
        UInt32 withdrawalsOffset = 32 + BitConverter.ToUInt32(data[offset..(offset + 4)]);
        offset += 4;

        payload.BlobGasUsed = BitConverter.ToUInt64(data[offset..(offset + 8)]);
        offset += 8;
        payload.ExcessBlobGas = BitConverter.ToUInt64(data[offset..(offset + 8)]);
        offset += 8;

        payload.ExtraData = data[(int)extraDataOffset..(int)transactionsOffset];

        payload.Transactions = DecodeTransactions(data[(int)transactionsOffset..(int)withdrawalsOffset]);
        payload.Withdrawals = Array.Empty<Withdrawal>();

        return payload;
    }

    byte[][] DecodeTransactions(byte[] data)
    {
        UInt32 firstTxOffset = BitConverter.ToUInt32(data[..4]);
        UInt32 txCount = firstTxOffset / 4;
        byte[][] txs = new byte[txCount][];
        int previous = (int)firstTxOffset;
        for (int i = 0; i < txCount; i++)
        {
            int next = i + 1 < txCount
                ? (int)BitConverter.ToUInt32(data[(i * 4 + 4)..(i * 4 + 8)])
                : data.Length;
            txs[i] = data[previous..next];
            previous = next;
        }

        return txs;
    }

    public byte[] EncodePayload(ExecutionPayloadV3 payload)
    {
        throw new System.NotImplementedException();
    }
}
