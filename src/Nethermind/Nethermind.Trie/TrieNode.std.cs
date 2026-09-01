// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadBlockAndFlags() => Volatile.Read(ref _blockAndFlags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ExchangeBlockAndFlags(byte newValue, byte comparand)
            => Interlocked.CompareExchange(ref _blockAndFlags, newValue, comparand);

        /// <summary>
        /// Atomically read _rlp using seqlock: retry if a concurrent write is detected.
        /// Memory barriers ensure ARM64 correctness (matching SeqlockCache/KeccakCache patterns).
        /// </summary>
        private CappedArray<byte> ReadRlp()
        {
            SpinWait spin = default;
            ulong seqBefore, seqAfter;
            byte[]? array;
            while (true)
            {
                seqBefore = Volatile.Read(ref _rlpSeqAndLength);
                if ((seqBefore >> 32 & 1) != 0) { spin.SpinOnce(); continue; }
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                array = _rlpArray;
                if (!Sse.IsSupported) Interlocked.MemoryBarrier();
                seqAfter = Volatile.Read(ref _rlpSeqAndLength);
                if (seqBefore == seqAfter) break;
                spin.SpinOnce();
            }

            return array is null ? default : new CappedArray<byte>(array, (int)(seqBefore & 0xFFFFFFFF));
        }

        /// <summary>
        /// Atomically write _rlp using seqlock: odd sequence signals write-in-progress.
        /// CAS on even sequences only — if another writer is active (odd), spin until it completes.
        /// Last writer wins: all writers write the same resolved data for a given node.
        /// Sequence uses bits 1-31 (31 bits, ~2 billion writes before wrap); bit 0 is the lock flag.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // CAS dominates latency; avoid code bloat at 5+ call sites
        internal void WriteRlp(CappedArray<byte> value)
        {
            SpinWait spin = default;
            while (true)
            {
                ulong current = Volatile.Read(ref _rlpSeqAndLength);
                ulong seq = current >> 32;
                if ((seq & 1) != 0)
                {
                    // Another writer is active — spin until it completes
                    spin.SpinOnce();
                    continue;
                }
                // Set lock bit (odd) — seq | 1 is always odd regardless of overflow
                ulong writing = (seq | 1) << 32;
                if (Interlocked.CompareExchange(ref _rlpSeqAndLength, writing, current) == current)
                {
                    Volatile.Write(ref _rlpArray, value.UnderlyingArray);
                    // Advance sequence by 2 and clear lock bit (even), store final length
                    ulong doneSeq = (seq + 2) & 0xFFFFFFFE;
                    Volatile.Write(ref _rlpSeqAndLength, doneSeq << 32 | (uint)value.Length);
                    return;
                }
                spin.SpinOnce(); // CAS failed — another writer raced; back off before retry
            }
        }
    }
}
