// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr
{
    public abstract class EnrContentEntry
    {
        /// <summary>
        /// A key string of the node record entry.
        /// </summary>
        public abstract string Key { get; }

        internal virtual string SortKey => GetSortKey(Key);

        // EIP-778 orders raw key bytes. Latin-1 maps each byte to one ordinal code unit.
        internal static string GetSortKey(string key) => Ascii.IsValid(key) ? key : Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(key));

        internal virtual int GetRlpLength() => Rlp.LengthOf(Key) + GetRlpLengthOfValue();

        /// <summary>
        /// Needed for optimized RLP serialization.
        /// </summary>
        /// <returns></returns>
        protected abstract int GetRlpLengthOfValue();

        /// <summary>
        /// Encodes the entry into a value RLP writer.
        /// </summary>
        public virtual void Encode(ref RlpWriter writer)
        {
            writer.Encode(Key);
            EncodeValue(ref writer);
        }

        protected abstract void EncodeValue(ref RlpWriter writer);

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
