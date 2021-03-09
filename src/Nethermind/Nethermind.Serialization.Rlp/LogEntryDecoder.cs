//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Serialization.Rlp
{
    public class LogEntryDecoder : IRlpStreamDecoder<LogEntry>, IRlpValueDecoder<LogEntry>
    {
        public static LogEntryDecoder Instance { get; } = new();

        public LogEntry? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }
            
            rlpStream.ReadSequenceLength();
            Address? address = rlpStream.DecodeAddress();
            long sequenceLength = rlpStream.ReadSequenceLength();
            Keccak[] topics = new Keccak[sequenceLength / 33];
            for (int i = 0; i < topics.Length; i++)
            {
                topics[i] = rlpStream.DecodeKeccak();
            }

            byte[] data = rlpStream.DecodeByteArray();

            return new LogEntry(address, data, topics);
        }

        public LogEntry? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                return null;
            }
            
            decoderContext.ReadSequenceLength();
            Address? address = decoderContext.DecodeAddress();
            long sequenceLength = decoderContext.ReadSequenceLength();
            Keccak[] topics = new Keccak[sequenceLength / 33];
            for (int i = 0; i < topics.Length; i++)
            {
                topics[i] = decoderContext.DecodeKeccak();
            }

            byte[] data = decoderContext.DecodeByteArray();

            return new LogEntry(address, data, topics);
        }

        public Rlp Encode(LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream rlpStream, LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            var (total, topics) = GetContentLength(item);
            rlpStream.StartSequence(total);
            
            rlpStream.Encode(item.LoggersAddress);
            rlpStream.StartSequence(topics);

            for (var i = 0; i < item.Topics.Length; i++)
            {
                rlpStream.Encode(item.Topics[i]);
            }
            
            rlpStream.Encode(item.Data);   
        }
        
        public int GetLength(LogEntry? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return 1;
            }
            
            return Rlp.LengthOfSequence(GetContentLength(item).Total);
        }
        
        private static (int Total, int Topics) GetContentLength(LogEntry? item)
        {
            var contentLength = 0;
            if (item == null)
            {
                return (contentLength, 0);
            }

            contentLength += Rlp.LengthOf(item.LoggersAddress);
            
            int topicsLength = GetTopicsLength(item);
            contentLength += Rlp.GetSequenceRlpLength(topicsLength);
            contentLength += Rlp.LengthOf(item.Data);
            
            return (contentLength, topicsLength);
        }
        
        private static int GetTopicsLength(LogEntry? item)
        {
            if (item == null)
            {
                return 0;
            }
            
            int topicsLength = 0;
            for (int i = 0; i < item.Topics.Length; i++)
            {
                topicsLength += Rlp.LengthOf(item.Topics[i]);
            }
            
            return topicsLength;
        }

        public static void DecodeStructRef(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors storage, out LogEntryStructRef item)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                item = new LogEntryStructRef();
                return;
            }
            
            decoderContext.ReadSequenceLength();
            decoderContext.DecodeAddressStructRef(out var address);
            var peekPrefixAndContentLength = decoderContext.PeekPrefixAndContentLength();
            var sequenceLength = peekPrefixAndContentLength.PrefixLength + peekPrefixAndContentLength.ContentLength;
            var topics = decoderContext.Data.Slice(decoderContext.Position, sequenceLength);
            decoderContext.SkipItem();
            var data = decoderContext.DecodeByteArraySpan();

            item = new LogEntryStructRef(address, data, topics);
        }
    }
}
