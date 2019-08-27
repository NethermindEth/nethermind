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
using System.IO;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Encoding
{
    public class ReceiptDecoder : IRlpDecoder<TxReceipt>
    {
        public TxReceipt Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }
            
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            TxReceipt txReceipt = new TxReceipt();
            rlpStream.ReadSequenceLength();
            byte[] firstItem = rlpStream.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            if(isStorage) txReceipt.BlockHash = rlpStream.DecodeKeccak();
            if(isStorage) txReceipt.BlockNumber = (long)rlpStream.DecodeUInt256();
            if(isStorage) txReceipt.Index = rlpStream.DecodeInt();
            if(isStorage) txReceipt.Sender = rlpStream.DecodeAddress();
            if(isStorage) txReceipt.Recipient = rlpStream.DecodeAddress();
            if(isStorage) txReceipt.ContractAddress = rlpStream.DecodeAddress();
            if(isStorage) txReceipt.GasUsed = (long) rlpStream.DecodeUBigInt();
            txReceipt.GasUsedTotal = (long) rlpStream.DecodeUBigInt();
            txReceipt.Bloom = rlpStream.DecodeBloom();

            int lastCheck = rlpStream.ReadSequenceLength() + rlpStream.Position;
            List<LogEntry> logEntries = new List<LogEntry>();

            while (rlpStream.Position < lastCheck)
            {
                logEntries.Add(Rlp.Decode<LogEntry>(rlpStream, RlpBehaviors.AllowExtraData));
            }

            bool allowExtraData = (rlpBehaviors & RlpBehaviors.AllowExtraData) != 0;
            if (!allowExtraData)
            {
                rlpStream.Check(lastCheck);
            }
            
            // since error was added later we can only rely on it in cases where we read receipt only and no data follows
            if (isStorage && !allowExtraData && rlpStream.Position != rlpStream.Length)
            {
                txReceipt.Error = rlpStream.DecodeString();
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;

            if (isStorage)
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
                    Rlp.Encode(item.Logs),
                    Rlp.Encode(item.Error));
            }

            return Rlp.Encode(
                rlpBehaviors.HasFlag(RlpBehaviors.Eip658Receipts) ? Rlp.Encode(item.StatusCode) : Rlp.Encode(item.PostTransactionState),
                Rlp.Encode(item.GasUsedTotal),
                Rlp.Encode(item.Bloom),
                Rlp.Encode(item.Logs));
        }

        public void Encode(RlpStream rlpStream, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                rlpStream.EncodeNullObject();
                return;
            }
            
            var (totalLength, logsLength) = GetContentLength(item, rlpBehaviors);
            
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            rlpStream.StartSequence(totalLength);
            if (isEip658receipts)
            {
                rlpStream.Encode(item.StatusCode);
            }
            else
            {
                rlpStream.Encode(item.PostTransactionState);
            }

            if (isStorage)
            {
                rlpStream.Encode(item.BlockHash);
                rlpStream.Encode(item.BlockNumber);
                rlpStream.Encode(item.Index);
                rlpStream.Encode(item.Sender);
                rlpStream.Encode(item.Recipient);
                rlpStream.Encode(item.ContractAddress);
                rlpStream.Encode(item.GasUsed);
                rlpStream.Encode(item.GasUsedTotal);
                rlpStream.Encode(item.Bloom);
                
                rlpStream.StartSequence(logsLength);

                for (var i = 0; i < item.Logs.Length; i++)
                {
                    rlpStream.Encode(item.Logs[i]);
                }
                
                rlpStream.Encode(item.Error);
            }
            else
            {
                rlpStream.Encode(item.GasUsedTotal);
                rlpStream.Encode(item.Bloom);
                
                rlpStream.StartSequence(logsLength);

                for (var i = 0; i < item.Logs.Length; i++)
                {
                    rlpStream.Encode(item.Logs[i]);
                }
            }    
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
        
        public void Encode(MemoryStream stream, TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.Write(Rlp.OfEmptySequence.Bytes);
                return;
            }
            
            var (totalLength, logsLength) = GetContentLength(item, rlpBehaviors);
            
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            Rlp.StartSequence(stream, totalLength);
            if (isEip658receipts)
            {
                Rlp.Encode(stream, item.StatusCode);
            }
            else
            {
                Rlp.Encode(stream, item.PostTransactionState);
            }

            if (isStorage)
            {
                Rlp.Encode(stream, item.BlockHash);
                Rlp.Encode(stream, item.BlockNumber);
                Rlp.Encode(stream, item.Index);
                Rlp.Encode(stream, item.Sender);
                Rlp.Encode(stream, item.Recipient);
                Rlp.Encode(stream, item.ContractAddress);
                Rlp.Encode(stream, item.GasUsed);
                Rlp.Encode(stream, item.GasUsedTotal);
                Rlp.Encode(stream, item.Bloom);
                
                Rlp.StartSequence(stream, logsLength);

                for (var i = 0; i < item.Logs.Length; i++)
                {
                    Rlp.Encode(stream, item.Logs[i]);
                }
                
                Rlp.Encode(stream, item.Error);
            }
            else
            {
                Rlp.Encode(stream, item.GasUsedTotal);
                Rlp.Encode(stream, item.Bloom);
                
                Rlp.StartSequence(stream, logsLength);

                for (var i = 0; i < item.Logs.Length; i++)
                {
                    Rlp.Encode(stream, item.Logs[i]);
                }
            }
        }
        
        private (int Total, int Logs) GetContentLength(TxReceipt item, RlpBehaviors rlpBehaviors)
        {
            var contentLength = 0;
            var logsLength = 0;
            if (item == null)
            {
                return (contentLength, 0);
            }
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;

            if (isStorage)
            {
                contentLength += Rlp.LengthOf(item.BlockHash);
                contentLength += Rlp.LengthOf(item.BlockNumber);
                contentLength += Rlp.LengthOf(item.Index);
                contentLength += Rlp.LengthOf(item.Sender);
                contentLength += Rlp.LengthOf(item.Recipient);
                contentLength += Rlp.LengthOf(item.ContractAddress);
                contentLength += Rlp.LengthOf(item.GasUsed);
                contentLength += Rlp.LengthOf(item.Error);
            }
            
            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            contentLength += Rlp.LengthOf(item.Bloom);

            logsLength = GetLogsLength(item);
            contentLength += Rlp.GetSequenceRlpLength(logsLength);

            bool isEip658receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (isEip658receipts)
            {
                contentLength += Rlp.LengthOf(item.StatusCode);
            }
            else
            {
                contentLength += Rlp.LengthOf(item.PostTransactionState);
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
    }
}