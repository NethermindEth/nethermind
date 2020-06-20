﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public class Bloom : IEquatable<Bloom>
    {
        public static readonly Bloom Empty = new Bloom();
        public const int BitLength = 2048;
        public const int ByteLength = BitLength / 8;
        
        public Bloom()
        {
            Bytes = new byte[ByteLength];
        }
        
        public Bloom(LogEntry[] logEntries, Bloom blockBloom = null)
        {
            Bytes = new byte[ByteLength];
            Add(logEntries, blockBloom);
        }

        public Bloom(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public void Set(byte[] sequence)
        {
            Set(sequence, null);
        }
        
        private void Set(byte[] sequence, Bloom masterBloom)
        {
            if (ReferenceEquals(this, Empty))
            {
                throw new InvalidOperationException("An attempt was made to update Bloom.Empty constant");
            }
            
            BloomExtract indexes = GetExtract(sequence);
            Set(indexes.Index1);
            Set(indexes.Index2);
            Set(indexes.Index3);
            if (masterBloom != null)
            {
                masterBloom.Set(indexes.Index1);
                masterBloom.Set(indexes.Index2);
                masterBloom.Set(indexes.Index3);
            }
        }
        
        public bool Matches(byte[] sequence)
        {
            BloomExtract indexes = GetExtract(sequence);
            return Matches(ref indexes);
        }
        
        public override string ToString()
        {
            return Bytes.ToHexString();
            // StringBuilder stringBuilder = new StringBuilder();
            //
            // for (int i = 0; i < _bits.Count; i++)
            // {
            //     char c = _bits[i] ? '1' : '0';
            //     stringBuilder.Append(c);
            // }
            //
            // return stringBuilder.ToString();
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

            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
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
            return Bytes.GetSimplifiedHashCode();
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

        public void Accumulate(Bloom bloom)
        {
            Bytes.AsSpan().Or(bloom.Bytes);
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

        private bool Get(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            return Bytes[bytePosition].GetBit(shift);
        }
        
        internal void Set(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            Bytes[bytePosition].SetBit(shift);
        }
        
        public bool Matches(Address address) => Matches(address.Bytes);
        
        public bool Matches(Keccak topic) => Matches(topic.Bytes);
        
        public bool Matches(ref BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

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

        public BloomStructRef ToStructRef() => new BloomStructRef(Bytes);
    }

    public ref struct BloomStructRef
    {
        public const int BitLength = 2048;
        public const int ByteLength = BitLength / 8;

        public BloomStructRef(LogEntry[] logEntries, Bloom blockBloom = null)
        {
            Bytes = new byte[ByteLength];
            Add(logEntries, blockBloom);
        }

        public BloomStructRef(Span<byte> bytes)
        {
            Bytes = bytes;
        }

        public Span<byte> Bytes { get; }

        public void Set(byte[] sequence)
        {
            Set(sequence, null);
        }

        private void Set(Span<byte> sequence, Bloom masterBloom = null)
        {
            Bloom.BloomExtract indexes = GetExtract(sequence);
            Set(indexes.Index1);
            Set(indexes.Index2);
            Set(indexes.Index3);
            if (masterBloom != null)
            {
                masterBloom.Set(indexes.Index1);
                masterBloom.Set(indexes.Index2);
                masterBloom.Set(indexes.Index3);
            }
        }

        public bool Matches(byte[] sequence)
        {
            Bloom.BloomExtract indexes = GetExtract(sequence);
            return Matches(ref indexes);
        }

        public override string ToString()
        {
            return Bytes.ToHexString();
            // StringBuilder stringBuilder = new StringBuilder();
            //
            // for (int i = 0; i < _bits.Count; i++)
            // {
            //     char c = _bits[i] ? '1' : '0';
            //     stringBuilder.Append(c);
            // }
            //
            // return stringBuilder.ToString();
        }

        public static bool operator !=(BloomStructRef a, Bloom b) => !(a == b);
        public static bool operator ==(BloomStructRef a, Bloom b) => a.Equals(b);
        
        public static bool operator !=(Bloom a, BloomStructRef b) => !(a == b);
        public static bool operator ==(Bloom a, BloomStructRef b) => b.Equals(a);
        
        public static bool operator !=(BloomStructRef a, BloomStructRef b) => !(a == b);
        public static bool operator ==(BloomStructRef a, BloomStructRef b) => a.Equals(b);


        public bool Equals(Bloom other)
        {
            if (ReferenceEquals(null, other)) return false;
            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }
        
        public bool Equals(BloomStructRef other)
        {
            return Nethermind.Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }


        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (obj.GetType() != typeof(BloomStructRef)) return false;
            return Equals((Bloom) obj);
        }

        public override int GetHashCode()
        {
            return Bytes.GetSimplifiedHashCode();
        }

        public void Add(LogEntry[] logEntries, Bloom blockBloom)
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

        public void Accumulate(BloomStructRef bloom)
        {
            Bytes.Or(bloom.Bytes);
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

        private bool Get(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            return Bytes[bytePosition].GetBit(shift);
        }

        private void Set(int index)
        {
            int bytePosition = index / 8;
            int shift = index % 8;
            Bytes[bytePosition].SetBit(shift);
        }

        public bool Matches(Address address) => Matches(address.Bytes);

        public bool Matches(Keccak topic) => Matches(topic.Bytes);

        public bool Matches(ref Bloom.BloomExtract extract) => Get(extract.Index1) && Get(extract.Index2) && Get(extract.Index3);

        public bool Matches(Bloom.BloomExtract extract) => Matches(ref extract);

        public static Bloom.BloomExtract GetExtract(Address address) => GetExtract(address.Bytes);

        public static Bloom.BloomExtract GetExtract(Keccak topic) => GetExtract(topic.Bytes);

        private static Bloom.BloomExtract GetExtract(Span<byte> sequence)
        {
            int GetIndex(Span<byte> bytes, int index1, int index2)
            {
                return 2047 - ((bytes[index1] << 8) + bytes[index2]) % 2048;
            }

            var keccakBytes = ValueKeccak.Compute(sequence).BytesAsSpan;
            var indexes = new Bloom.BloomExtract(GetIndex(keccakBytes, 0, 1), GetIndex(keccakBytes, 2, 3), GetIndex(keccakBytes, 4, 5));
            return indexes;
        }
    }
}