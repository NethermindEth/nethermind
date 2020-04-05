//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
#pragma warning disable 618

namespace Nethermind.Serialization.Rlp
{
    public class ReceiptStorageDecoder : IRlpDecoder<TxReceipt>, IRlpValueDecoder<TxReceipt>
    {
        private readonly bool _supportTxHash;
        private const byte MarkTxHashByte = 255;

        public ReceiptStorageDecoder(bool supportTxHash = true)
        {
            _supportTxHash = supportTxHash;
        }
        
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
            
            if (!allowExtraData)
            {
                if (isStorage && _supportTxHash)
                {
                    // since txHash was added later and may not be in rlp, we provide special mark byte that it will be next
                    if (rlpStream.PeekByte() == MarkTxHashByte)
                    {
                        rlpStream.ReadByte();
                        txReceipt.TxHash = rlpStream.DecodeKeccak();
                    }
                }

                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (rlpStream.Position != rlpStream.Length)
                {
                    txReceipt.Error = rlpStream.DecodeString();
                }
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public TxReceipt Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }
            
            bool isStorage = (rlpBehaviors & RlpBehaviors.Storage) != 0;
            TxReceipt txReceipt = new TxReceipt();
            decoderContext.ReadSequenceLength();
            byte[] firstItem = decoderContext.DecodeByteArray();
            if (firstItem.Length == 1)
            {
                txReceipt.StatusCode = firstItem[0];
            }
            else
            {
                txReceipt.PostTransactionState = firstItem.Length == 0 ? null : new Keccak(firstItem);
            }

            if(isStorage) txReceipt.BlockHash = decoderContext.DecodeKeccak();
            if(isStorage) txReceipt.BlockNumber = (long)decoderContext.DecodeUInt256();
            if(isStorage) txReceipt.Index = decoderContext.DecodeInt();
            if(isStorage) txReceipt.Sender = decoderContext.DecodeAddress();
            if(isStorage) txReceipt.Recipient = decoderContext.DecodeAddress();
            if(isStorage) txReceipt.ContractAddress = decoderContext.DecodeAddress();
            if(isStorage) txReceipt.GasUsed = (long) decoderContext.DecodeUBigInt();
            txReceipt.GasUsedTotal = (long) decoderContext.DecodeUBigInt();
            txReceipt.Bloom = decoderContext.DecodeBloom();

            int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            List<LogEntry> logEntries = new List<LogEntry>();

            while (decoderContext.Position < lastCheck)
            {
                logEntries.Add(Rlp.Decode<LogEntry>(ref decoderContext, RlpBehaviors.AllowExtraData));
            }

            bool allowExtraData = (rlpBehaviors & RlpBehaviors.AllowExtraData) != 0;
            if (!allowExtraData)
            {
                decoderContext.Check(lastCheck);
            }
            
            if (!allowExtraData)
            {
                if (isStorage && _supportTxHash)
                {
                    // since txHash was added later and may not be in rlp, we provide special mark byte that it will be next
                    if (decoderContext.PeekByte() == MarkTxHashByte)
                    {
                        decoderContext.ReadByte();
                        txReceipt.TxHash = decoderContext.DecodeKeccak();
                    }
                }

                // since error was added later we can only rely on it in cases where we read receipt only and no data follows, empty errors might not be serialized
                if (decoderContext.Position != decoderContext.Length)
                {
                    txReceipt.Error = decoderContext.DecodeString();
                }
            }

            txReceipt.Logs = logEntries.ToArray();
            return txReceipt;
        }

        public Rlp Encode(TxReceipt item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
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

                if (_supportTxHash)
                {
                    rlpStream.WriteByte(MarkTxHashByte);
                    rlpStream.Encode(item.TxHash);
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

                rlpStream.Encode(item.Error);
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
                contentLength += 1 + Rlp.LengthOf(item.TxHash);
            }
            
            contentLength += Rlp.LengthOf(item.GasUsedTotal);
            contentLength += Rlp.LengthOf(item.Bloom);

            logsLength = GetLogsLength(item);
            contentLength += Rlp.GetSequenceRlpLength(logsLength);

            bool isEip658Receipts = (rlpBehaviors & RlpBehaviors.Eip658Receipts) == RlpBehaviors.Eip658Receipts;

            if (isEip658Receipts)
            {
                contentLength += Rlp.LengthOf(item.StatusCode);
            }
            else
            {
                contentLength += Rlp.LengthOf(item.PostTransactionState);
            }

            contentLength += Rlp.LengthOf(item.Error);

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