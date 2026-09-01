// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        // Single-threaded guest: no publication or compare-and-swap is needed, so both collapse to
        // plain field access and inline into the flag properties, which are read per node touch.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadBlockAndFlags() => _blockAndFlags;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ExchangeBlockAndFlags(byte newValue, byte comparand)
        {
            _blockAndFlags = newValue;
            return comparand;
        }

        /// <summary>
        /// Read _rlp directly &mdash; see the std counterpart for the seqlock this replaces.
        /// </summary>
        /// <remarks>
        /// No writer can race a reader in the guest, so the seqlock collapses to two loads. Being
        /// this small also lets it inline into the several call sites that read the RLP per node.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private CappedArray<byte> ReadRlp()
        {
            byte[]? array = _rlpArray;
            return array is null ? default : new CappedArray<byte>(array, (int)(uint)_rlpSeqAndLength);
        }

        /// <summary>
        /// Write _rlp directly &mdash; see the std counterpart for the seqlock this replaces.
        /// </summary>
        /// <remarks>
        /// No competing writer in the guest, so the publish is just the two field stores.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteRlp(CappedArray<byte> value) => InitRlp(value);
    }
}
