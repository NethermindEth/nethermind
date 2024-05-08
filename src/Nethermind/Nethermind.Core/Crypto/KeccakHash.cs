// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static System.Numerics.BitOperations;
using static Nethermind.Core.Crypto.KeccakHash;

// ReSharper disable InconsistentNaming
namespace Nethermind.Core.Crypto
{
    public sealed class KeccakHash
    {
        public const int HASH_SIZE = 32;
        private const int STATE_SIZE = 200;
        private const int HASH_DATA_AREA = 136;
        private const int ROUNDS = 24;
        private const int LANE_BITS = 8 * 8;
        private const int TEMP_BUFF_SIZE = 144;

        private static readonly ulong[] RoundConstants =
        {
            0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL,
            0x8000000080008000UL, 0x000000000000808bUL, 0x0000000080000001UL,
            0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008aUL,
            0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
            0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL,
            0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
            0x000000000000800aUL, 0x800000008000000aUL, 0x8000000080008081UL,
            0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
        };

        private byte[] _remainderBuffer = Array.Empty<byte>();
        private StateBox? _stateBox = null;
        private byte[]? _hash;
        private int _remainderLength;
        private int _roundSize;

        private static int GetRoundSize(int hashSize) => checked(STATE_SIZE - 2 * hashSize);

        /// <summary>
        /// Indicates the hash size in bytes.
        /// </summary>
        public int HashSize { get; private set; }

        /// <summary>
        /// The current hash buffer at this point. Recomputed after hash updates.
        /// </summary>
        public byte[] Hash => _hash ??= GenerateHash(); // If the hash is null, recalculate.

        private KeccakHash(KeccakHash original)
        {
            if (original._stateBox is not null)
            {
                _stateBox = Pool.RentState();
                _stateBox.state = original._stateBox.state;
            }

            if (original._remainderBuffer.Length > 0)
            {
                _remainderBuffer = Pool.RentRemainder();
                original._remainderBuffer.AsSpan().CopyTo(_remainderBuffer);
            }

            _roundSize = original._roundSize;
            _remainderLength = original._remainderLength;
            HashSize = original.HashSize;
        }

        private KeccakHash(int size)
        {
            // Verify the size
            if (size != 32 && size != 64)
            {
                WrongSizeKeccak();
            }

            _roundSize = STATE_SIZE == size ? HASH_DATA_AREA : checked(STATE_SIZE - (2 * size));
            _remainderLength = 0;
            HashSize = size;
        }

        public KeccakHash Copy() => new(this);

        public static KeccakHash Create(int size = HASH_SIZE) => new(size);

        // update the state with given number of rounds
        private static void KeccakF(ref KeccakBuffer st)
        {
            ulong bCa, bCe, bCi, bCo, bCu;
            ulong da, de, di, @do, du;
            ulong eba, ebe, ebi, ebo, ebu;
            ulong ega, ege, egi, ego, egu;
            ulong eka, eke, eki, eko, eku;
            ulong ema, eme, emi, emo, emu;
            ulong esa, ese, esi, eso, esu;

            for (int round = 0; round < ROUNDS; round += 2)
            {
                //    prepareTheta
                bCa = st.aba() ^ st.aga() ^ st.aka() ^ st.ama() ^ st.asa();
                bCe = st.abe() ^ st.age() ^ st.ake() ^ st.ame() ^ st.ase();
                bCi = st.abi() ^ st.agi() ^ st.aki() ^ st.ami() ^ st.asi();
                bCo = st.abo() ^ st.ago() ^ st.ako() ^ st.amo() ^ st.aso();
                bCu = st.abu() ^ st.agu() ^ st.aku() ^ st.amu() ^ st.asu();

                //thetaRhoPiChiIotaPrepareTheta(round  , A, E)
                da = bCu ^ RotateLeft(bCe, 1);
                de = bCa ^ RotateLeft(bCi, 1);
                di = bCe ^ RotateLeft(bCo, 1);
                @do = bCi ^ RotateLeft(bCu, 1);
                du = bCo ^ RotateLeft(bCa, 1);

                st.aba() ^= da;
                bCa = st.aba();
                st.age() ^= de;
                bCe = RotateLeft(st.age(), 44);
                st.aki() ^= di;
                bCi = RotateLeft(st.aki(), 43);
                st.amo() ^= @do;
                bCo = RotateLeft(st.amo(), 21);
                st.asu() ^= du;
                bCu = RotateLeft(st.asu(), 14);
                eba = bCa ^ ((~bCe) & bCi);
                eba ^= RoundConstants[round];
                ebe = bCe ^ ((~bCi) & bCo);
                ebi = bCi ^ ((~bCo) & bCu);
                ebo = bCo ^ ((~bCu) & bCa);
                ebu = bCu ^ ((~bCa) & bCe);

                st.abo() ^= @do;
                bCa = RotateLeft(st.abo(), 28);
                st.agu() ^= du;
                bCe = RotateLeft(st.agu(), 20);
                st.aka() ^= da;
                bCi = RotateLeft(st.aka(), 3);
                st.ame() ^= de;
                bCo = RotateLeft(st.ame(), 45);
                st.asi() ^= di;
                bCu = RotateLeft(st.asi(), 61);
                ega = bCa ^ ((~bCe) & bCi);
                ege = bCe ^ ((~bCi) & bCo);
                egi = bCi ^ ((~bCo) & bCu);
                ego = bCo ^ ((~bCu) & bCa);
                egu = bCu ^ ((~bCa) & bCe);

                st.abe() ^= de;
                bCa = RotateLeft(st.abe(), 1);
                st.agi() ^= di;
                bCe = RotateLeft(st.agi(), 6);
                st.ako() ^= @do;
                bCi = RotateLeft(st.ako(), 25);
                st.amu() ^= du;
                bCo = RotateLeft(st.amu(), 8);
                st.asa() ^= da;
                bCu = RotateLeft(st.asa(), 18);
                eka = bCa ^ ((~bCe) & bCi);
                eke = bCe ^ ((~bCi) & bCo);
                eki = bCi ^ ((~bCo) & bCu);
                eko = bCo ^ ((~bCu) & bCa);
                eku = bCu ^ ((~bCa) & bCe);

                st.abu() ^= du;
                bCa = RotateLeft(st.abu(), 27);
                st.aga() ^= da;
                bCe = RotateLeft(st.aga(), 36);
                st.ake() ^= de;
                bCi = RotateLeft(st.ake(), 10);
                st.ami() ^= di;
                bCo = RotateLeft(st.ami(), 15);
                st.aso() ^= @do;
                bCu = RotateLeft(st.aso(), 56);
                ema = bCa ^ ((~bCe) & bCi);
                eme = bCe ^ ((~bCi) & bCo);
                emi = bCi ^ ((~bCo) & bCu);
                emo = bCo ^ ((~bCu) & bCa);
                emu = bCu ^ ((~bCa) & bCe);

                st.abi() ^= di;
                bCa = RotateLeft(st.abi(), 62);
                st.ago() ^= @do;
                bCe = RotateLeft(st.ago(), 55);
                st.aku() ^= du;
                bCi = RotateLeft(st.aku(), 39);
                st.ama() ^= da;
                bCo = RotateLeft(st.ama(), 41);
                st.ase() ^= de;
                bCu = RotateLeft(st.ase(), 2);
                esa = bCa ^ ((~bCe) & bCi);
                ese = bCe ^ ((~bCi) & bCo);
                esi = bCi ^ ((~bCo) & bCu);
                eso = bCo ^ ((~bCu) & bCa);
                esu = bCu ^ ((~bCa) & bCe);

                //    prepareTheta
                bCa = eba ^ ega ^ eka ^ ema ^ esa;
                bCe = ebe ^ ege ^ eke ^ eme ^ ese;
                bCi = ebi ^ egi ^ eki ^ emi ^ esi;
                bCo = ebo ^ ego ^ eko ^ emo ^ eso;
                bCu = ebu ^ egu ^ eku ^ emu ^ esu;

                //thetaRhoPiChiIotaPrepareTheta(round+1, E, A)
                da = bCu ^ RotateLeft(bCe, 1);
                de = bCa ^ RotateLeft(bCi, 1);
                di = bCe ^ RotateLeft(bCo, 1);
                @do = bCi ^ RotateLeft(bCu, 1);
                du = bCo ^ RotateLeft(bCa, 1);

                eba ^= da;
                bCa = eba;
                ege ^= de;
                bCe = RotateLeft(ege, 44);
                eki ^= di;
                bCi = RotateLeft(eki, 43);
                emo ^= @do;
                bCo = RotateLeft(emo, 21);
                esu ^= du;
                bCu = RotateLeft(esu, 14);
                st.aba() = bCa ^ ((~bCe) & bCi);
                st.aba() ^= RoundConstants[round + 1];
                st.abe() = bCe ^ ((~bCi) & bCo);
                st.abi() = bCi ^ ((~bCo) & bCu);
                st.abo() = bCo ^ ((~bCu) & bCa);
                st.abu() = bCu ^ ((~bCa) & bCe);

                ebo ^= @do;
                bCa = RotateLeft(ebo, 28);
                egu ^= du;
                bCe = RotateLeft(egu, 20);
                eka ^= da;
                bCi = RotateLeft(eka, 3);
                eme ^= de;
                bCo = RotateLeft(eme, 45);
                esi ^= di;
                bCu = RotateLeft(esi, 61);
                st.aga() = bCa ^ ((~bCe) & bCi);
                st.age() = bCe ^ ((~bCi) & bCo);
                st.agi() = bCi ^ ((~bCo) & bCu);
                st.ago() = bCo ^ ((~bCu) & bCa);
                st.agu() = bCu ^ ((~bCa) & bCe);

                ebe ^= de;
                bCa = RotateLeft(ebe, 1);
                egi ^= di;
                bCe = RotateLeft(egi, 6);
                eko ^= @do;
                bCi = RotateLeft(eko, 25);
                emu ^= du;
                bCo = RotateLeft(emu, 8);
                esa ^= da;
                bCu = RotateLeft(esa, 18);
                st.aka() = bCa ^ ((~bCe) & bCi);
                st.ake() = bCe ^ ((~bCi) & bCo);
                st.aki() = bCi ^ ((~bCo) & bCu);
                st.ako() = bCo ^ ((~bCu) & bCa);
                st.aku() = bCu ^ ((~bCa) & bCe);

                ebu ^= du;
                bCa = RotateLeft(ebu, 27);
                ega ^= da;
                bCe = RotateLeft(ega, 36);
                eke ^= de;
                bCi = RotateLeft(eke, 10);
                emi ^= di;
                bCo = RotateLeft(emi, 15);
                eso ^= @do;
                bCu = RotateLeft(eso, 56);
                st.ama() = bCa ^ ((~bCe) & bCi);
                st.ame() = bCe ^ ((~bCi) & bCo);
                st.ami() = bCi ^ ((~bCo) & bCu);
                st.amo() = bCo ^ ((~bCu) & bCa);
                st.amu() = bCu ^ ((~bCa) & bCe);

                ebi ^= di;
                bCa = RotateLeft(ebi, 62);
                ego ^= @do;
                bCe = RotateLeft(ego, 55);
                eku ^= du;
                bCi = RotateLeft(eku, 39);
                ema ^= da;
                bCo = RotateLeft(ema, 41);
                ese ^= de;
                bCu = RotateLeft(ese, 2);
                st.asa() = bCa ^ ((~bCe) & bCi);
                st.ase() = bCe ^ ((~bCi) & bCo);
                st.asi() = bCi ^ ((~bCo) & bCu);
                st.aso() = bCo ^ ((~bCu) & bCa);
                st.asu() = bCu ^ ((~bCa) & bCe);
            }
        }

        public static byte[] ComputeHashBytes(ReadOnlySpan<byte> input, int size = HASH_SIZE)
        {
            if (size != 32 && size != 64)
            {
                WrongSizeKeccak();
            }

            byte[] output = new byte[size];
            ComputeHash(input, output);
            return output;
        }

        public static void ComputeHashBytesToSpan(ReadOnlySpan<byte> input, Span<byte> output)
        {
            ComputeHash(input, output);
        }

        public static void ComputeUIntsToUint(Span<uint> input, Span<uint> output)
        {
            ComputeHash(MemoryMarshal.Cast<uint, byte>(input), MemoryMarshal.Cast<uint, byte>(output));
        }

        public static uint[] ComputeUIntsToUint(Span<uint> input, int size)
        {
            uint[] output = new uint[size / sizeof(uint)];
            ComputeUIntsToUint(input, output);
            return output;
        }

        public static uint[] ComputeBytesToUint(ReadOnlySpan<byte> input, int size)
        {
            uint[] output = new uint[size / sizeof(uint)];
            ComputeHash(input, MemoryMarshal.Cast<uint, byte>(output));
            return output;
        }

        // compute a Keccak hash (md) of given byte length from "in"
        [SkipLocalsInit]
        public static void ComputeHash(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int size = output.Length;
            // 136 or 72
            int roundSize = GetRoundSize(size);
            if ((uint)output.Length > STATE_SIZE)
            {
                ThrowBadKeccak();
            }

            KeccakBuffer state = default;
            Span<byte> temp = stackalloc byte[TEMP_BUFF_SIZE];
            temp.Clear();

            int remainingInputLength = input.Length;
            for (; remainingInputLength >= roundSize; remainingInputLength -= roundSize, input = input[roundSize..])
            {
                ReadOnlySpan<ulong> input64 = MemoryMarshal.Cast<byte, ulong>(input[..roundSize]);

                for (int i = 0; i < input64.Length; i++)
                {
                    state[i] ^= input64[i];
                }

                KeccakF(ref state);
            }

            // last block and padding
            if (input.Length >= TEMP_BUFF_SIZE || input.Length > roundSize || roundSize + 1 >= TEMP_BUFF_SIZE || roundSize == 0 || roundSize - 1 >= TEMP_BUFF_SIZE)
            {
                ThrowBadKeccak();
            }

            input[..remainingInputLength].CopyTo(temp);
            temp[remainingInputLength] = 1;
            temp[roundSize - 1] |= 0x80;

            Span<ulong> tempU64 = MemoryMarshal.Cast<byte, ulong>(temp[..roundSize]);
            for (int i = 0; i < tempU64.Length; i++)
            {
                state[i] ^= tempU64[i];
            }

            KeccakF(ref state);
            if (size == 32)
            {
                Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(output)) = Unsafe.As<KeccakBuffer, Vector256<byte>>(ref state);
            }
            else // size 64
            {
                Unsafe.As<byte, Vector512<byte>>(ref MemoryMarshal.GetReference(output)) = Unsafe.As<KeccakBuffer, Vector512<byte>>(ref state);
            }
        }

        public void Update(ReadOnlySpan<byte> input)
        {
            if (_hash is not null)
            {
                ThrowHashingComplete();
            }

            // If the size is zero, quit
            if (input.Length == 0)
            {
                return;
            }

            // If our provided state is empty, initialize a new one
            StateBox? stateBox = _stateBox;
            if (stateBox is null)
            {
                _stateBox = stateBox = Pool.RentState();
            }
            ref KeccakBuffer state = ref stateBox.state;

            // If our remainder is non zero.
            if (_remainderLength != 0)
            {
                // Copy data to our remainder
                ReadOnlySpan<byte> remainderAdditive = input[..Math.Min(input.Length, _roundSize - _remainderLength)];
                remainderAdditive.CopyTo(_remainderBuffer.AsSpan(_remainderLength));

                // Increment the length
                _remainderLength += remainderAdditive.Length;

                // Increment the input
                input = input[remainderAdditive.Length..];

                // If our remainder length equals a full round
                if (_remainderLength == _roundSize)
                {
                    // Cast our input to ulongs.
                    Span<ulong> remainderBufferU64 = MemoryMarshal.Cast<byte, ulong>(_remainderBuffer.AsSpan(0, _roundSize));

                    // Eliminate bounds check for state for the loop
                    _ = state[remainderBufferU64.Length];
                    // Loop for each ulong in this remainder, and xor the state with the input.
                    for (int i = 0; i < remainderBufferU64.Length; i++)
                    {
                        state[i] ^= remainderBufferU64[i];
                    }

                    // Perform our KeccakF on our state.
                    KeccakF(ref state);

                    // Clear remainder fields
                    _remainderLength = 0;
                    Pool.ReturnRemainder(ref _remainderBuffer);
                }
            }

            // Loop for every round in our size.
            while (input.Length >= _roundSize)
            {
                // Cast our input to ulongs.
                ReadOnlySpan<ulong> input64 = MemoryMarshal.Cast<byte, ulong>(input[.._roundSize]);

                // Eliminate bounds check for state for the loop
                _ = state[input64.Length];
                // Loop for each ulong in this round, and xor the state with the input.
                for (int i = 0; i < input64.Length; i++)
                {
                    state[i] ^= input64[i];
                }

                // Perform our KeccakF on our state.
                KeccakF(ref state);

                // Remove the input data processed this round.
                input = input[_roundSize..];
            }

            // last block and padding
            if (input.Length >= TEMP_BUFF_SIZE || input.Length > _roundSize || _roundSize + 1 >= TEMP_BUFF_SIZE || _roundSize == 0 || _roundSize - 1 >= TEMP_BUFF_SIZE)
            {
                ThrowBadKeccak();
            }

            // If we have any remainder here, it means any remainder was processed before, we can copy our data over and set our length
            if (input.Length > 0)
            {
                if (_remainderBuffer.Length == 0)
                {
                    _remainderBuffer = Pool.RentRemainder();
                }
                input.CopyTo(_remainderBuffer);
                _remainderLength = input.Length;
            }
        }

        private byte[] GenerateHash()
        {
            byte[] output = new byte[HashSize];
            UpdateFinalTo(output);
            // Obtain the state data in the desired (hash) size we want.
            _hash = output;

            // Return the result.
            return output;
        }

        public void UpdateFinalTo(Span<byte> output)
        {
            if (_hash is not null)
            {
                ThrowHashingComplete();
            }
            
            StateBox? stateBox = _stateBox;
            ref KeccakBuffer state = ref stateBox!.state;
            if (_remainderLength > 0)
            {
                Span<byte> remainder = _remainderBuffer.AsSpan(0, _roundSize);
                // Set a 1 byte after the remainder.
                remainder[_remainderLength++] = 1;

                // Set the highest bit on the last byte.
                remainder[_roundSize - 1] |= 0x80;

                // Cast the remainder buffer to ulongs.
                Span<ulong> temp64 = MemoryMarshal.Cast<byte, ulong>(remainder);
                // Loop for each ulong in this round, and xor the state with the input.
                for (int i = 0; i < temp64.Length; i++)
                {
                    state[i] ^= temp64[i];
                }

                Pool.ReturnRemainder(ref _remainderBuffer);
            }
            else
            {
                Span<byte> temp = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref state, 1));
                // Xor 1 byte as first byte.
                temp[0] ^= 1;
                // Xor the highest bit on the last byte.
                temp[_roundSize - 1] ^= 0x80;
            }

            KeccakF(ref state);

            // Obtain the state data in the desired (hash) size we want.
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref state, 1))[..HashSize].CopyTo(output);

            Pool.ReturnState(ref _stateBox);
        }

        public void Reset()
        {
            // Clear our hash state information.
            Pool.ReturnState(ref _stateBox);
            Pool.ReturnRemainder(ref _remainderBuffer);
            _hash = null;
        }

        public void ResetTo(KeccakHash original)
        {
            if (original._remainderBuffer.Length == 0)
            {
                Pool.ReturnRemainder(ref _remainderBuffer);
            }
            else
            {
                if (_remainderBuffer.Length == 0)
                {
                    _remainderBuffer = Pool.RentRemainder();
                }
                original._remainderBuffer.AsSpan().CopyTo(_remainderBuffer);
            }

            _roundSize = original._roundSize;
            _remainderLength = original._remainderLength;

            if (original._stateBox is null)
            {
                Pool.ReturnState(ref _stateBox);
            }
            else
            {
                if (_stateBox is null)
                {
                    // Original allocated, but not here, so allocated
                    _stateBox = Pool.RentState();
                }
                // Copy from original
                _stateBox.state = original._stateBox.state;
            }

            HashSize = original.HashSize;
            _hash = null;
        }

        [DoesNotReturn]
        private static void ThrowBadKeccak() => throw new ArgumentException("Bad Keccak use");

        [DoesNotReturn]
        private static void ThrowHashingComplete() => throw new InvalidOperationException("Keccak hash is complete");

        private static class Pool
        {
            private const int MaxPooledPerThread = 4;
            [ThreadStatic]
            private static Queue<byte[]>? s_remainderCache;
            public static byte[] RentRemainder() => s_remainderCache?.TryDequeue(out byte[]? remainder) ?? false ? remainder : new byte[STATE_SIZE];
            public static void ReturnRemainder(ref byte[] remainder)
            {
                if (remainder.Length == 0) return;

                var cache = (s_remainderCache ??= new());
                if (cache.Count <= MaxPooledPerThread)
                {
                    remainder.AsSpan().Clear();
                    cache.Enqueue(remainder);
                }

                remainder = Array.Empty<byte>();
            }

            [ThreadStatic]
            private static Queue<StateBox>? s_stateCache;
            public static StateBox RentState() => s_stateCache?.TryDequeue(out StateBox? state) ?? false ? state : new StateBox();
            public static void ReturnState(ref StateBox? state)
            {
                if (state is null) return;

                var cache = (s_stateCache ??= new());
                if (cache.Count <= MaxPooledPerThread)
                {
                    state.state = default;
                    cache.Enqueue(state);
                }

                state = null!;
            }
        }
        public class StateBox
        {
            public KeccakBuffer state;
        }

        [InlineArray(KeccakBufferCount)]
        public struct KeccakBuffer
        {
            public const int KeccakBufferCount = 25;
            private ulong st;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void WrongSizeKeccak()
        {
            throw new ArgumentException($"Invalid Keccak hash size. Must be 32 or 64.");
        }
    }

    public static class BufferExtensions
    {
        public static ref ulong aba(ref this KeccakBuffer buffer) => ref buffer[0];
        public static ref ulong abe(ref this KeccakBuffer buffer) => ref buffer[1];
        public static ref ulong abi(ref this KeccakBuffer buffer) => ref buffer[2];
        public static ref ulong abo(ref this KeccakBuffer buffer) => ref buffer[3];
        public static ref ulong abu(ref this KeccakBuffer buffer) => ref buffer[4];
        public static ref ulong aga(ref this KeccakBuffer buffer) => ref buffer[5];
        public static ref ulong age(ref this KeccakBuffer buffer) => ref buffer[6];
        public static ref ulong agi(ref this KeccakBuffer buffer) => ref buffer[7];
        public static ref ulong ago(ref this KeccakBuffer buffer) => ref buffer[8];
        public static ref ulong agu(ref this KeccakBuffer buffer) => ref buffer[9];
        public static ref ulong aka(ref this KeccakBuffer buffer) => ref buffer[10];
        public static ref ulong ake(ref this KeccakBuffer buffer) => ref buffer[11];
        public static ref ulong aki(ref this KeccakBuffer buffer) => ref buffer[12];
        public static ref ulong ako(ref this KeccakBuffer buffer) => ref buffer[13];
        public static ref ulong aku(ref this KeccakBuffer buffer) => ref buffer[14];
        public static ref ulong ama(ref this KeccakBuffer buffer) => ref buffer[15];
        public static ref ulong ame(ref this KeccakBuffer buffer) => ref buffer[16];
        public static ref ulong ami(ref this KeccakBuffer buffer) => ref buffer[17];
        public static ref ulong amo(ref this KeccakBuffer buffer) => ref buffer[18];
        public static ref ulong amu(ref this KeccakBuffer buffer) => ref buffer[19];
        public static ref ulong asa(ref this KeccakBuffer buffer) => ref buffer[20];
        public static ref ulong ase(ref this KeccakBuffer buffer) => ref buffer[21];
        public static ref ulong asi(ref this KeccakBuffer buffer) => ref buffer[22];
        public static ref ulong aso(ref this KeccakBuffer buffer) => ref buffer[23];
        public static ref ulong asu(ref this KeccakBuffer buffer) => ref buffer[24];
    }
}
