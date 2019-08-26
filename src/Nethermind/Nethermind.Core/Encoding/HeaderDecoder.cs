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

using System;
using System.IO;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Encoding
{
    public class HeaderDecoder : IRlpValueDecoder<BlockHeader>, IRlpDecoder<BlockHeader>
    {
        public BlockHeader Decode(Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                return null;
            }

            var headerRlp = decoderContext.PeekNextItem();
            int headerSequenceLength = decoderContext.ReadSequenceLength();
            int headerCheck = decoderContext.Position + headerSequenceLength;

            Keccak parentHash = decoderContext.DecodeKeccak();
            Keccak ommersHash = decoderContext.DecodeKeccak();
            Address beneficiary = decoderContext.DecodeAddress();
            Keccak stateRoot = decoderContext.DecodeKeccak();
            Keccak transactionsRoot = decoderContext.DecodeKeccak();
            Keccak receiptsRoot = decoderContext.DecodeKeccak();
            Bloom bloom = decoderContext.DecodeBloom();
            UInt256 difficulty = decoderContext.DecodeUInt256();
            UInt256 number = decoderContext.DecodeUInt256();
            UInt256 gasLimit = decoderContext.DecodeUInt256();
            UInt256 gasUsed = decoderContext.DecodeUInt256();
            UInt256 timestamp = decoderContext.DecodeUInt256();
            byte[] extraData = decoderContext.DecodeByteArray();

            BlockHeader blockHeader = new BlockHeader(
                parentHash,
                ommersHash,
                beneficiary,
                difficulty,
                (long) number,
                (long) gasLimit,
                timestamp,
                extraData)
            {
                StateRoot = stateRoot,
                TxRoot = transactionsRoot,
                ReceiptsRoot = receiptsRoot,
                Bloom = bloom,
                GasUsed = (long) gasUsed,
                Hash = Keccak.Compute(headerRlp)
            };
            
            if (decoderContext.PeekPrefixAndContentLength().ContentLength == Keccak.Size)
            {
                blockHeader.MixHash = decoderContext.DecodeKeccak();
                blockHeader.Nonce = (ulong) decoderContext.DecodeUBigInt();
            }
            else
            {
                blockHeader.AuRaStep = (long) decoderContext.DecodeUInt256();
                blockHeader.AuRaSignature = decoderContext.DecodeByteArray();
            }
            
            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                decoderContext.Check(headerCheck);
            }

            return blockHeader;
        }

        public BlockHeader Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            var headerRlp = rlpStream.PeekNextItem();
            int headerSequenceLength = rlpStream.ReadSequenceLength();
            int headerCheck = rlpStream.Position + headerSequenceLength;

            Keccak parentHash = rlpStream.DecodeKeccak();
            Keccak ommersHash = rlpStream.DecodeKeccak();
            Address beneficiary = rlpStream.DecodeAddress();
            Keccak stateRoot = rlpStream.DecodeKeccak();
            Keccak transactionsRoot = rlpStream.DecodeKeccak();
            Keccak receiptsRoot = rlpStream.DecodeKeccak();
            Bloom bloom = rlpStream.DecodeBloom();
            UInt256 difficulty = rlpStream.DecodeUInt256();
            UInt256 number = rlpStream.DecodeUInt256();
            UInt256 gasLimit = rlpStream.DecodeUInt256();
            UInt256 gasUsed = rlpStream.DecodeUInt256();
            UInt256 timestamp = rlpStream.DecodeUInt256();
            byte[] extraData = rlpStream.DecodeByteArray();

            BlockHeader blockHeader = new BlockHeader(
                parentHash,
                ommersHash,
                beneficiary,
                difficulty,
                (long) number,
                (long) gasLimit,
                timestamp,
                extraData)
            {
                StateRoot = stateRoot,
                TxRoot = transactionsRoot,
                ReceiptsRoot = receiptsRoot,
                Bloom = bloom,
                GasUsed = (long) gasUsed,
                Hash = Keccak.Compute(headerRlp)
            };
            
            if (rlpStream.PeekPrefixAndContentLength().ContentLength == Keccak.Size)
            {
                blockHeader.MixHash = rlpStream.DecodeKeccak();
                blockHeader.Nonce = (ulong) rlpStream.DecodeUBigInt();
            }
            else
            {
                blockHeader.AuRaStep = (long) rlpStream.DecodeUInt256();
                blockHeader.AuRaSignature = rlpStream.DecodeByteArray();
            }

            if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            {
                rlpStream.Check(headerCheck);
            }

            return blockHeader;
        }

        public void Encode(RlpStream rlpStream, BlockHeader item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                rlpStream.EncodeNullObject();
                return;
            }
            
            bool notForSealing = !rlpBehaviors.HasFlag(RlpBehaviors.ForSealing);
            rlpStream.StartSequence(GetContentLength(item, rlpBehaviors));
            rlpStream.Encode(item.ParentHash);
            rlpStream.Encode(item.OmmersHash);
            rlpStream.Encode(item.Beneficiary);
            rlpStream.Encode(item.StateRoot);
            rlpStream.Encode(item.TxRoot);
            rlpStream.Encode(item.ReceiptsRoot);
            rlpStream.Encode(item.Bloom);
            rlpStream.Encode(item.Difficulty);
            rlpStream.Encode(item.Number);
            rlpStream.Encode(item.GasLimit);
            rlpStream.Encode(item.GasUsed);
            rlpStream.Encode(item.Timestamp);
            rlpStream.Encode(item.ExtraData);

            if (notForSealing)
            {
                bool isAuRa = item.AuRaSignature != null;
                
                if (isAuRa)
                {
                    rlpStream.Encode(item.AuRaStep.Value);
                    rlpStream.Encode(item.AuRaSignature);
                }
                else
                {
	                rlpStream.Encode(item.MixHash);
    	            rlpStream.Encode(item.Nonce);
                }
            }
        }

        public Rlp Encode(BlockHeader item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }
            
            RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }
        
        public void Encode(MemoryStream stream, BlockHeader item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.Write(Rlp.OfEmptySequence.Bytes);
                return;
            }

            bool notForSealing = !rlpBehaviors.HasFlag(RlpBehaviors.ForSealing);
            Rlp.StartSequence(stream, GetContentLength(item, rlpBehaviors));
            Rlp.Encode(stream, item.ParentHash);
            Rlp.Encode(stream, item.OmmersHash);
            Rlp.Encode(stream, item.Beneficiary);
            Rlp.Encode(stream, item.StateRoot);
            Rlp.Encode(stream, item.TxRoot);
            Rlp.Encode(stream, item.ReceiptsRoot);
            Rlp.Encode(stream, item.Bloom);
            Rlp.Encode(stream, item.Difficulty);
            Rlp.Encode(stream, item.Number);
            Rlp.Encode(stream, item.GasLimit);
            Rlp.Encode(stream, item.GasUsed);
            Rlp.Encode(stream, item.Timestamp);
            Rlp.Encode(stream, item.ExtraData);

            if (notForSealing)
            {
                bool isAuRa = item.AuRaSignature != null;
                
                if (isAuRa)
                {
                    Rlp.Encode(stream, item.AuRaStep.Value);
                    Rlp.Encode(stream, item.AuRaSignature);
                }
                else
                {
                    Rlp.Encode(stream, item.MixHash);
                    Rlp.Encode(stream, item.Nonce);
                }
                
            }
        }

        private int GetContentLength(BlockHeader item, RlpBehaviors rlpBehaviors)
        {
            if (item == null)
            {
                return 0;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            int contentLength = 0
                                + Rlp.LengthOf(item.ParentHash)
                                + Rlp.LengthOf(item.OmmersHash)
                                + Rlp.LengthOf(item.Beneficiary)
                                + Rlp.LengthOf(item.StateRoot)
                                + Rlp.LengthOf(item.TxRoot)
                                + Rlp.LengthOf(item.ReceiptsRoot)
                                + Rlp.LengthOf(item.Bloom)
                                + Rlp.LengthOf(item.Difficulty)
                                + Rlp.LengthOf(item.Number)
                                + Rlp.LengthOf(item.GasLimit)
                                + Rlp.LengthOf(item.GasUsed)
                                + Rlp.LengthOf(item.Timestamp)
                                + Rlp.LengthOf(item.ExtraData);

            if (notForSealing)
            {
                var isAUra = item.AuRaSignature != null;
                
                if (isAUra)
                {
                    contentLength += Rlp.LengthOf(item.AuRaStep.Value);
                    contentLength += Rlp.LengthOf(item.AuRaSignature);
                }
                else
                {
                    contentLength += Rlp.LengthOf(item.MixHash);
                    contentLength += Rlp.LengthOf(item.Nonce);
                }
            }

            return contentLength;
        }

        public int GetLength(BlockHeader item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }
    }
}