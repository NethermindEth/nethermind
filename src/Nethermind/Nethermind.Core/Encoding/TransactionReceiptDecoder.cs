/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Encoding
{
    public class TransactionReceiptDecoder : IRlpDecoder<TransactionReceipt>
    {
        public TransactionReceipt Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            bool isStorage = rlpBehaviors.HasFlag(RlpBehaviors.Storage);
            TransactionReceipt receipt = new TransactionReceipt();
            context.ReadSequenceLength();
            byte[] firstItem = context.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                receipt.StatusCode = firstItem[0];
            }
            else
            {
                receipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            if(isStorage) receipt.BlockHash = context.DecodeKeccak();
            if(isStorage) receipt.BlockNumber = context.DecodeUInt256();
            if(isStorage) receipt.Index = context.DecodeInt();
            if(isStorage) receipt.Sender = context.DecodeAddress();
            if(isStorage) receipt.Recipient = context.DecodeAddress();
            if(isStorage) receipt.ContractAddress = context.DecodeAddress();
            if(isStorage) receipt.GasUsed = (long) context.DecodeUBigInt();
            receipt.GasUsedTotal = (long) context.DecodeUBigInt();
            receipt.Bloom = context.DecodeBloom();

            int lastCheck = context.ReadSequenceLength() + context.Position;
            List<LogEntry> logEntries = new List<LogEntry>();

            while (context.Position < lastCheck)
            {
                logEntries.Add(Rlp.Decode<LogEntry>(context, RlpBehaviors.AllowExtraData));
            }

            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                context.Check(lastCheck);
            }

            receipt.Logs = logEntries.ToArray();
            return receipt;
        }

        public Rlp Encode(TransactionReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.Storage))
            {
                return Rlp.Encode(
                    rlpBehaviors.HasFlag(RlpBehaviors.Eip658Receipts) ? Rlp.Encode(item.StatusCode) : Rlp.Encode(item.PostTransactionState),
                    Rlp.Encode(item.BlockHash),
                    Rlp.Encode(item.BlockNumber),
                    Rlp.Encode(item.Index),
                    Rlp.Encode(item.Sender),
                    Rlp.Encode(item.Recipient),
                    Rlp.Encode(item.ContractAddress),
                    Rlp.Encode(item.GasUsed),
                    Rlp.Encode(item.GasUsedTotal),
                    Rlp.Encode(item.Bloom),
                    Rlp.Encode(item.Logs));
            }

            return Rlp.Encode(
                rlpBehaviors.HasFlag(RlpBehaviors.Eip658Receipts) ? Rlp.Encode(item.StatusCode) : Rlp.Encode(item.PostTransactionState),
                Rlp.Encode(item.GasUsedTotal),
                Rlp.Encode(item.Bloom),
                Rlp.Encode(item.Logs));
        }
    }
}