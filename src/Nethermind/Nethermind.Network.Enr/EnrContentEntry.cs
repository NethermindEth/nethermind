// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            ValueRlpWriter writer = new(rlpStream);
            Encode(ref writer);
        }

        /// <summary>
        /// Encodes the entry into a value RLP writer.
        /// </summary>
        public void Encode(ref ValueRlpWriter writer)
        {
            writer.Encode(Key);
            EncodeValue(ref writer);
        }

        protected abstract void EncodeValue(ref ValueRlpWriter writer);

        public override int GetHashCode() => Key.GetHashCode();
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
