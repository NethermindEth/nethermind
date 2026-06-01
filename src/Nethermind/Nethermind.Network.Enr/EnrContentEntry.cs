// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr
{
    public abstract class EnrContentEntry
    {
        /// <summary>
        /// A key string of the node record entry.
        /// </summary>
        public abstract string Key { get; }

        internal int GetRlpLength() => Rlp.LengthOf(Key) + GetRlpLengthOfValue();

        /// <summary>
        /// Needed for optimized RLP serialization.
        /// </summary>
        /// <returns></returns>
        protected abstract int GetRlpLengthOfValue();

        /// <summary>
        /// Encodes the entry into an RLP stream. 
        /// </summary>
        public void Encode(RlpStream rlpStream)
        {
            rlpStream.Encode(Key);
            EncodeValue(rlpStream);
        }

        /// <summary>
        /// Encodes the entry into a span-backed buffer.
        /// </summary>
        public void Encode(Span<byte> buffer, ref int position)
        {
            position = EncodeAscii(buffer, position, Key);
            EncodeValue(buffer, ref position);
        }

        protected abstract void EncodeValue(RlpStream rlpStream);

        protected abstract void EncodeValue(Span<byte> buffer, ref int position);

        protected static void EncodeInteger(Span<byte> buffer, ref int position, long value)
        {
            int length = Rlp.LengthOf(value);
            Rlp.Encode(value, buffer.Slice(position, length));
            position += length;
        }

        public override int GetHashCode() => Key.GetHashCode();

        private static int EncodeAscii(Span<byte> buffer, int position, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Rlp.Encode(buffer, position, ReadOnlySpan<byte>.Empty);
            }

            int byteCount = Encoding.ASCII.GetByteCount(value);
            if (byteCount <= 128)
            {
                Span<byte> bytes = stackalloc byte[byteCount];
                Encoding.ASCII.GetBytes(value.AsSpan(), bytes);
                return Rlp.Encode(buffer, position, bytes);
            }

            return Rlp.Encode(buffer, position, Encoding.ASCII.GetBytes(value));
        }
    }

    /// <summary>
    /// Single key, value pair entry in the ENR record content.
    /// </summary>
    [DebuggerDisplay("{Key} {Value}")]
    public abstract class EnrContentEntry<TValue>(TValue value) : EnrContentEntry
    {
        /// <summary>
        /// A value of the node record entry.
        /// </summary>
        public TValue Value { get; } = value;

        public override string ToString() => $"{Key} {Value}";
    }
}
