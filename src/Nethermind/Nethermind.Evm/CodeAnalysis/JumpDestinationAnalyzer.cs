// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis
{
    public sealed class JumpDestinationAnalyzer(byte[] code)
    {
        private const int PUSH1 = 0x60;
        private const int PUSH32 = 0x7f;
        private const int JUMPDEST = 0x5b;
        private const int BEGINSUB = 0x5c;
        private const int BitShiftPerInt64 = 6;

        private long[]? _jumpDestBitmap;
        public byte[] MachineCode { get; } = code;

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            // Take array ref to local so Jit knows its size won't change in the method.
            byte[] machineCode = MachineCode;
            _jumpDestBitmap ??= CreateJumpDestinationBitmap(machineCode);

            var result = false;
            // Cast to uint to change negative numbers to very int high numbers
            // Then do length check, this both reduces check by 1 and eliminates the bounds
            // check from accessing the array.
            if ((uint)destination < (uint)machineCode.Length &&
                IsJumpDestination(_jumpDestBitmap, destination))
            {
                // Store byte to int, as less expensive operations at word size
                int codeByte = machineCode[destination];
                if (isSubroutine)
                {
                    result = codeByte == BEGINSUB;
                }
                else
                {
                    result = codeByte == JUMPDEST;
                }
            }

            return result;
        }

        /// <summary>
        /// Used for conversion between different representations of bit array.
        /// Returns (n + (64 - 1)) / 64, rearranged to avoid arithmetic overflow.
        /// For example, in the bit to int case, the straightforward calc would
        /// be (n + 63) / 64, but that would cause overflow. So instead it's
        /// rearranged to ((n - 1) / 64) + 1.
        /// Due to sign extension, we don't need to special case for n == 0, if we use
        /// bitwise operations (since ((n - 1) >> 6) + 1 = 0).
        /// This doesn't hold true for ((n - 1) / 64) + 1, which equals 1.
        ///
        /// Usage:
        /// GetInt32ArrayLengthFromBitLength(77): returns how many ints must be
        /// allocated to store 77 bits.
        /// </summary>
        /// <param name="n"></param>
        /// <returns>how many ints are required to store n bytes</returns>
        private static int GetInt64ArrayLengthFromBitLength(int n)
        {
            return (int)((uint)(n - 1 + (1 << BitShiftPerInt64)) >> BitShiftPerInt64);
        }

        /// <summary>
        /// Collects data locations in code.
        /// An unset bit means the byte is an opcode, a set bit means it's data.
        /// </summary>
        private static long[] CreateJumpDestinationBitmap(byte[] code)
        {
            long[] jumpDestBitmap = new long[GetInt64ArrayLengthFromBitLength(code.Length)];

            int pc = 0;
            long flags = 0;
            while (true)
            {
                // Since we are using a non-standard for loop here;
                // changing to while(true) plus below if check elides
                // the bounds check from the following code array access.
                if ((uint)pc >= (uint)code.Length) break;

                // Grab the instruction from the code.
                int op = code[pc];

                int move = 1;
                if ((uint)op - JUMPDEST <= BEGINSUB - JUMPDEST)
                {
                    // Accumulate JumpDest to register
                    flags |= 1L << pc;
                }
                else if ((uint)op - PUSH1 <= PUSH32 - PUSH1)
                {
                    // Skip forward amount of data the push represents
                    // don't need to analyse data for JumpDests
                    move = op - PUSH1 + 2;
                }

                int next = pc + move;
                if ((pc & 0x3F) + move > 0x3f || next >= code.Length)
                {
                    // Moving to next array element (or finishing) assign to array.
                    MarkJumpDestinations(jumpDestBitmap, pc, flags);
                    flags = 0;
                }

                // Next instruction
                pc = next;
            }

            return jumpDestBitmap;
        }

        /// <summary>
        /// Checks if the position is in a code segment.
        /// </summary>
        private static bool IsJumpDestination(long[] bitvec, int pos)
        {
            int vecIndex = pos >> BitShiftPerInt64;
            // Check if in bounds, Jit will add slightly more expensive exception throwing check if we don't
            if ((uint)vecIndex >= (uint)bitvec.Length) return false;

            return (bitvec[vecIndex] & (1L << pos)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkJumpDestinations(long[] bitvec, int pos, long flags)
        {
            uint offset = (uint)pos >> BitShiftPerInt64;
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bitvec), offset)
                |= flags;
        }
    }
}
