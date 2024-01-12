// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis
{
    public sealed class JumpDestinationAnalyzer
    {
        private const int PUSH1 = 0x60;
        private const int PUSH32 = 0x7f;
        private const int JUMPDEST = 0x5b;
        private const int BEGINSUB = 0x5c;

        private byte[]? _codeBitmap;
        public byte[] MachineCode { get; set; }

        public JumpDestinationAnalyzer(byte[] code)
        {
            MachineCode = code;
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            // Take array ref to local so Jit knows its size won't change in the method.
            byte[] machineCode = MachineCode;
            _codeBitmap ??= CreateJumpDestinationBitmap(machineCode);

            var result = false;
            // Cast to uint to change negative numbers to very high numbers
            // Then do length check, this both reduces check by 1 and eliminates the bounds
            // check from accessing the array.
            if ((uint)destination < (uint)machineCode.Length &&
                IsJumpDestination(_codeBitmap, destination))
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
        /// Collects data locations in code.
        /// An unset bit means the byte is an opcode, a set bit means it's data.
        /// </summary>
        private static byte[] CreateJumpDestinationBitmap(byte[] code)
        {
            byte[] bitvec = new byte[(code.Length / 8) + 1];

            int pc = 0;
            while (true)
            {
                // Since we are using a non-standard for loop here
                // Changing to while(true) plus below if check elides
                // the bounds check from the array access
                if ((uint)pc >= (uint)code.Length) break;
                int instruction = code[pc];

                if (instruction >= PUSH1 && instruction <= PUSH32)
                {
                    pc += instruction - PUSH1 + 2;
                }
                else if (instruction == JUMPDEST || instruction == BEGINSUB)
                {
                    Set(bitvec, pc);
                    pc++;
                }
                else
                {
                    pc++;
                }
            }

            return bitvec;
        }

        /// <summary>
        /// Checks if the position is in a code segment.
        /// </summary>
        private static bool IsJumpDestination(byte[] bitvec, int pos)
        {
            //return (bitvec[pos / 8] & (0x80 >> (pos % 8))) == 0;

            int vecIndex = pos >> 3;
            // Check if in bounds, Jit will add slightly more expensive exception throwing check if we don't
            if ((uint)vecIndex >= (uint)bitvec.Length) return false;

            // Store byte to int, as less expensive operations at word size
            int codeByte = bitvec[vecIndex];
            return (codeByte & (0x80 >> (pos & 7))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Set(byte[] bitvec, int pos)
        {
            int vecIndex = pos >> 3;
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bitvec), vecIndex)
                |= (byte)(1 << (7 - (pos & 7)));
        }
    }
}
