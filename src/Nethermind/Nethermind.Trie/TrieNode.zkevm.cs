// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        /// <summary>Reads <c>_blockAndFlags</c> directly &mdash; see the std counterpart for the acquire read this replaces.</summary>
        /// <remarks>
        /// Single-threaded guest: nothing to publish, so this collapses to a plain field load and inlines
        /// into the flag properties, which are read per node touch.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadBlockAndFlags() => _blockAndFlags;

        /// <summary>Stores <c>_blockAndFlags</c> and reports the exchange as having succeeded.</summary>
        /// <remarks>
        /// Keeps the shape of the compare-and-exchange it replaces &mdash; returning
        /// <paramref name="comparand"/> makes each caller's retry loop exit after one pass &mdash; but
        /// ignores it, so the store is unconditional. Callers must therefore read the field immediately
        /// before exchanging: no writer can race them in the guest, but a stale
        /// <paramref name="comparand"/> would be overwritten here rather than retried.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ExchangeBlockAndFlags(byte newValue, byte comparand)
        {
            _blockAndFlags = newValue;
            return comparand;
        }

        /// <summary>Reads <c>_rlpArray</c> for a presence check &mdash; see the std counterpart for the acquire read this replaces.</summary>
        /// <remarks>Read per child while encoding a branch, so the fence it drops is paid sixteen times per node.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? ReadRlpArray() => _rlpArray;

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
