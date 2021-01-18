﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    public class ReceiptMessageDecoder : IRlpDecoder<TxReceipt>
    {
        static ReceiptMessageDecoder()
        {
            Rlp.Decoders[typeof(TxReceipt)] = new ReceiptMessageDecoder();
        }
        
        public TxReceipt Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }
            
            TxReceipt txReceipt = new TxReceipt();
            _ = rlpStream.ReadSequenceLength();
            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1 && (firstItem[0] == 0 || firstItem[0] == 1))
            {
                txReceipt.StatusCode = firstItem[0];
                txReceipt.GasUsedTotal = (long) rlpStream.DecodeUBigInt();
            }
            else if (firstItem.Length >= 1 && firstItem.Length <= 4)
            {
                txReceipt.GasUsedTotal = (long) firstItem.ToUnsignedBigInteger();
                txReceipt.SkipStateAndStatusInRlp = true;
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
                txReceipt.GasUsedTotal = (long) rlpStream.DecodeUBigInt();
            }
            
            txReceipt.Bloom = rlpStream.DecodeBloom();

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;

            int numberOfReceipts = rlpStream.ReadNumberOfItemsRemaining(lastCheck);
            LogEntry[] entries = new LogEntry[numberOfReceipts];
            for (int i = 0; i < numberOfReceipts; i++)
            {
                entries[i] = Rlp.Decode<LogEntry>(rlpStream, RlpBehaviors.AllowExtraData);
            }

            txReceipt.Logs = entries;
            return txReceipt;
        }

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Rlp.Encode(
                (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts ? Rlp.Encode(item.StatusCode) : Rlp.Encode(item.PostTransactionState),
                Rlp.Encode(item.GasUsedTotal),
                Rlp.Encode(item.Bloom),
                Rlp.Encode(item.Logs));
        }

        private (int Total, int Logs) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            if (item == null)
            {
                return (0, 0);
            }
         
            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            contentLength += Rlp.LengthOf(item.Bloom);

            int logsLength = GetLogsLength(item);
            contentLength += Rlp.GetSequenceRlpLength(logsLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (!item.SkipStateAndStatusInRlp)
            {
                contentLength += isEip658Receipts 
                    ? Rlp.LengthOf(item.StatusCode) 
                    : Rlp.LengthOf(item.PostTransactionState);
            }

            return (contentLength, logsLength);
        }
        
        private int GetLogsLength(TxReceipt item)
        {
            int logsLength = 0;
            for (var i = 0; i < item.Logs.Length; i++)
            {
                logsLength += Rlp.LengthOf(item.Logs[i]);
            }
            
            return logsLength;
        }

        public int GetLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);
        }
        
        public byte[] EncodeNew(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence.Bytes;
            }
            
            var length = GetLength(item, rlpBehaviors);
            RlpStream stream = new RlpStream(length);
            Encode(stream, item, rlpBehaviors);
            return stream.Data;
        }
        
        public void Encode(RlpStream rlpStream, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                rlpStream.EncodeNullObject();
                return;
            }
            
            (int totalContentLength, int logsLength) = GetContentLength(item, rlpBehaviors);
            
            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            rlpStream.StartSequence(totalContentLength);
            if (!item.SkipStateAndStatusInRlp)
            {
                if (isEip658Receipts)
                {
                    rlpStream.Encode(item.StatusCode);
                }
                else
                {
                    rlpStream.Encode(item.PostTransactionState);
                }
            }

            rlpStream.Encode(item.GasUsedTotal);
            rlpStream.Encode(item.Bloom);
            
            rlpStream.StartSequence(logsLength);
            for (var i = 0; i < item.Logs.Length; i++)
            {
                rlpStream.Encode(item.Logs[i]);
            }
        }
    }
}
