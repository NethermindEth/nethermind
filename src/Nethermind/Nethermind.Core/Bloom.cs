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
        
        public Bloom(LogEntry[] logEntries, Bloom blockBloom)
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
            Span<byte> keccakBytes = ValueKeccak.Compute(sequence).BytesAsSpan;
            for (int i = 0; i < 6; i += 2)
            {
                int index = 2047 - ((keccakBytes[i] << 8) + keccakBytes[i + 1]) % 2048;
                _bits.Set(index, true);
                if (masterBloom != null)
                {
                    masterBloom._bits.Set(index, true);
                }
            }
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
        
        private void Add(LogEntry[] logEntries, Bloom blockBloom)
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
    }
}