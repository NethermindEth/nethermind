//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections;
using System.Reflection.PortableExecutable;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm
{
    public class CodeInfo
    {
        private byte[]? _codeBitmap;
        public byte[]? MachineCode { get; set; }
        public IPrecompile? Precompile { get; set; }
        
        public CodeInfo(byte[] code)
        {
            MachineCode = code;
        }

        public bool IsPrecompile => Precompile != null;
        
        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
        }
        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (destination < 0 || MachineCode == null || destination >= MachineCode.Length)
            {
                return false;
            }

            _codeBitmap ??= CodeInfoHelper.CreateCodeBitmap(MachineCode);

            if (!CodeInfoHelper.IsCodeSegment(_codeBitmap, destination))
            {
                return false;
            }
            
            if (isSubroutine)
            {
                return MachineCode[destination] == 0x5c;
            }

            return MachineCode[destination] == 0x5b;
        }
    }
    
    public static class CodeInfoHelper
    {
        private const UInt16 Set2BitsMask = 0b1100_0000_0000_0000;
        private const UInt16 Set3BitsMask = 0b1110_0000_0000_0000;
        private const UInt16 Set4BitsMask = 0b1111_0000_0000_0000;
        private const UInt16 Set5BitsMask = 0b1111_1000_0000_0000;
        private const UInt16 Set6BitsMask = 0b1111_1100_0000_0000;
        private const UInt16 Set7BitsMask = 0b1111_1110_0000_0000;

        private static readonly byte[] _lookup = new byte[8] { 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2, 0x1, };
        
        /// <summary>
        /// Collects data locations in code.
        /// An unset bit means the byte is an opcode, a set bit means it's data.
        /// </summary>
        public static byte[] CreateCodeBitmap(byte[] code)
        {
            // The bitmap is 4 bytes longer than necessary, in case the code
            // ends with a PUSH32, the algorithm will push zeroes onto the
            // bitvector outside the bounds of the actual code.
            byte[] bitvec = new byte[(code.Length / 8) + 1 + 4];
            
            byte push1 = 0x60;
            byte push32 = 0x7f;
            
            for (int pc = 0; pc < code.Length;)
            {
                byte op = code[pc];
                pc++;
                
                if (op < push1 || op > push32)
                {
                    continue;
                }

                int numbits = op - push1 + 1;
                
                if (numbits >= 8) {
                    for (; numbits >= 16; numbits -= 16)
                    {
                        bitvec.Set16(pc);
                        pc += 16;
                    }
                    for( ; numbits >= 8; numbits -= 8)
                    {
                        bitvec.Set8(pc);
                        pc += 8;
                    }
                }

                switch (numbits)
                {
                    case 1:
                        bitvec.Set1(pc);
                        pc += 1;
                        break;
                    case 2:
                        bitvec.SetN(pc, Set2BitsMask);
                        pc += 2;
                        break;
                    case 3:
                        bitvec.SetN(pc, Set3BitsMask);
                        pc += 3;
                        break;
                    case 4:
                        bitvec.SetN(pc, Set4BitsMask);
                        pc += 4;
                        break;
                    case 5:
                        bitvec.SetN(pc, Set5BitsMask);
                        pc += 5;
                        break;
                    case 6:
                        bitvec.SetN(pc,Set6BitsMask);
                        pc += 6;
                        break;
                    case 7:
                        bitvec.SetN(pc, Set7BitsMask);
                        pc += 7;
                        break;
                }
            }
            
            return bitvec;
        }

        /// <summary>
        /// Checks if the position is in a code segment.
        /// </summary>
        public static bool IsCodeSegment(byte[] bitvec, int pos)
        {
            return (bitvec[pos / 8] & (0x80 >> (pos % 8))) == 0;
        }

        private static void Set1(this byte[] bitvec, int pos)
        {
            bitvec[pos / 8] |= _lookup[pos % 8];
        }

        private static void SetN(this byte[] bitvec, int pos, UInt16 flag)
        {
            ushort a = (ushort)(flag >> (pos % 8));
            bitvec[pos / 8] |= (byte)(a >> 8);
            byte b = (byte)a;
            if (b != 0 ){
                //	If the bit-setting affects the neighbouring byte, we can assign - no need to OR it,
                //	since it's the first write to that byte
                bitvec[pos / 8 + 1] = b;
            }
        }

        private static void Set8(this byte[] bitvec, int pos)
        {
            byte a = (byte)(0xFF >> (pos % 8));
            bitvec[pos / 8] |= a;
            bitvec[pos / 8 + 1] = (byte)~a;
        }

        private static void Set16(this byte[] bitvec, int pos)
        {
            byte a = (byte)(0xFF >> (pos % 8));
            bitvec[pos / 8] |= a;
            bitvec[pos / 8 + 1] = 0xFF;
            bitvec[pos/8+2] = (byte)~a;
        }
    }
}
