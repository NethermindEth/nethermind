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
using System.Collections;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public class Bloom : IEquatable<Bloom>
    {
        public static readonly Bloom Empty = new Bloom();
        
        private static readonly byte[] EmptyBloomBytes = new byte[256];

        private readonly BitArray _bits;

        public Bloom()
        {
            _bits = new BitArray(2048);
        }
        
        public Bloom(LogEntry[] logEntries, Bloom blockBloom = null)
        {
            _bits = new BitArray(2048);
            Add(logEntries, blockBloom);
        }

        public Bloom(BitArray bitArray)
        {
            if (bitArray.Length != 2048)
            {
                throw new ArgumentException("Invalid source array length", nameof(bitArray));
            }
            
            _bits = bitArray;
        }

        public byte[] Bytes => ReferenceEquals(this, Empty) ?  EmptyBloomBytes : _bits.ToBytes();

        public void Set(byte[] sequence)
        {
            Set(sequence, null);
        }
        
        private void Set(byte[] sequence, Bloom masterBloom)
        {
            var indexes = GetExtract(sequence);
            _bits.Set(indexes.Index1, true);
            _bits.Set(indexes.Index2, true);
            _bits.Set(indexes.Index3, true);
            if (masterBloom != null)
            {
                masterBloom._bits.Set(indexes.Index1, true);
                masterBloom._bits.Set(indexes.Index2, true);
                masterBloom._bits.Set(indexes.Index3, true);
            }
        }
        
        public bool Matches(byte[] sequence)
        {
            var indexes = GetExtract(sequence);
            return Matches(ref indexes);
        }
        
        public override string ToString()
        {
            StringBuilder stringBuilder = new StringBuilder();

            for (int i = 0; i < _bits.Count; i++)
            {
                char c = _bits[i] ? '1' : '0';
                stringBuilder.Append(c);
            }

            return stringBuilder.ToString();
        }

        public static bool operator !=(Bloom a, Bloom b)
        {
            return !(a == b);
        }
        
        public static bool operator==(Bloom a, Bloom b)
        {
            if(ReferenceEquals(a, b))
            {
                return true;
            }
            
            return a?.Equals(b) ?? false;
        }
        
        public bool Equals(Bloom other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            if (_bits.Length != other._bits.Length)
            {
                return false;
            }
            
            for (int i = 0; i < _bits.Length; i++)
            {
                if (_bits[i] != other._bits[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Bloom)obj);
        }

        public override int GetHashCode()
        {
            return _bits != null ? _bits.GetHashCode() : 0;
        }
        
        public void Add(LogEntry[] logEntries, Bloom blockBloom = null)
        {
            for (int entryIndex = 0; entryIndex < logEntries.Length; entryIndex++)
            {
                LogEntry logEntry = logEntries[entryIndex];
                byte[] addressBytes = logEntry.LoggersAddress.Bytes;
                Set(addressBytes, blockBloom);
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    Keccak topic = logEntry.Topics[topicIndex];
                    Set(topic.Bytes, blockBloom);
                }
            }
        }
        
        public bool Matches(LogEntry logEntry)
        {
            if (Matches(logEntry.LoggersAddress))
            {
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    if (!Matches(logEntry.Topics[topicIndex]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        public bool Matches(Address address) => Matches(address.Bytes);
        
        public bool Matches(Keccak topic) => Matches(topic.Bytes);
        
        public bool Matches(ref BloomExtract extract) => _bits[extract.Index1] && _bits[extract.Index2] && _bits[extract.Index3];

        public bool Matches(BloomExtract extract) => Matches(ref extract);
        
        public static BloomExtract GetExtract(Address address) => GetExtract(address.Bytes);
        
        public  static BloomExtract GetExtract(Keccak topic) => GetExtract(topic.Bytes);

        private static BloomExtract GetExtract(byte[] sequence)
        {
            int GetIndex(Span<byte> bytes, int index1, int index2)
            {
                return 2047 - ((bytes[index1] << 8) + bytes[index2]) % 2048;
            }

            var keccakBytes = ValueKeccak.Compute(sequence).BytesAsSpan;
            var indexes = new BloomExtract(GetIndex(keccakBytes, 0, 1), GetIndex(keccakBytes, 2, 3), GetIndex(keccakBytes, 4, 5));
            return indexes;
        }
        
        public struct BloomExtract
        {
            public BloomExtract(int index1, int index2, int index3)
            {
                Index1 = index1;
                Index2 = index2;
                Index3 = index3;
            }
            
            public int Index1 { get; }
            public int Index2 { get; }
            public int Index3 { get; }
        }
    }
}