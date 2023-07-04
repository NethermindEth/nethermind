// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr
{
    public abstract class EnrContentEntry
    {
        /// <summary>
        /// A key string of the node record entry.
        /// </summary>
        public abstract string Key { get; }

        internal int GetRlpLength()
        {
            return Rlp.LengthOf(Key) + GetRlpLengthOfValue();
        }

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

        protected abstract void EncodeValue(RlpStream rlpStream);

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }
    }

    /// <summary>
    /// Single key, value pair entry in the ENR record content.
    /// </summary>
    [DebuggerDisplay("{Key} {Value}")]
    public abstract class EnrContentEntry<TValue> : EnrContentEntry
    {
        /// <summary>
        /// A value of the node record entry.
        /// </summary>
        public TValue Value { get; }

        protected EnrContentEntry(TValue value)
        {
            Value = value;
        }

        public override string ToString()
        {
            return $"{Key} {Value}";
        }
    }
}
