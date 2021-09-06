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
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm
{
    public class BitmapJumpdestAnalyzer : ICodeInfoAnalyzer
    {
        public byte[] MachineCode { get; set; }
        private ushort[]? _validJumpDestinations;
        private ushort[]? _validJumpSubDestinations;

        private ushort[] _lookup = new ushort[16] {32768, 16384, 8192, 4096, 2048, 1024, 512, 256, 128, 64, 32, 16, 8, 4, 2, 1 };

        public BitmapJumpdestAnalyzer(byte[] code)
        {
            MachineCode = code;
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_validJumpDestinations == null)
            {
                CalculateJumpDestinations();
            }

            if (destination < 0 || destination >= MachineCode.Length)
            {
                return false;
            }

            var bitmap = isSubroutine ? _validJumpSubDestinations : _validJumpDestinations;

            int byteNo = destination / 16;
            int bitNo = (destination % 16);
            var mask = _lookup[bitNo];

            return (bitmap[byteNo] & mask) == mask;
        }

        private void CalculateJumpDestinations()
        {
            _validJumpDestinations = new ushort[MachineCode.Length / 16 + 1];
             _validJumpSubDestinations = new ushort[MachineCode.Length / 16 + 1];

            int index = 0;
            ushort currByteValueJump = 0;
            ushort currByteValueBeginSub = 0;
            var currByteIndex = index / 16;
            while (index < MachineCode.Length)
            {
                byte instruction = MachineCode[index];

                //if (instruction == Instruction.JUMPDEST
                if (instruction == 0x5b || instruction == 0x5c)
                {
                    int newByteIndex = index / 16;

                    if (newByteIndex > currByteIndex)
                    {
                        if (currByteValueJump > 0)
                        {
                            _validJumpDestinations[currByteIndex] = currByteValueJump;
                            currByteValueJump = 0;
                        }

                        if (currByteValueBeginSub > 0)
                        {
                            _validJumpSubDestinations[currByteIndex] = currByteValueBeginSub;
                            currByteValueBeginSub = 0;
                        }
                        
                        currByteIndex = newByteIndex;
                    }

                    if (instruction == 0x5b)
                    {
                        currByteValueJump += _lookup[index % 16];
                    }
                    
                    if(instruction == 0x5c)
                    {
                        currByteValueBeginSub += _lookup[index % 16];
                    }
                }

                if (instruction >= 0x60 && instruction <= 0x7f)
                {
                    //index += instruction - Instruction.PUSH1 + 2;
                    index += instruction - 0x60 + 2;
                }
                else
                {
                    index++;
                }
            }

            if (currByteValueJump > 0)
            {
                _validJumpDestinations[currByteIndex] = currByteValueJump;
            }
            if (currByteValueBeginSub > 0)
            {
                _validJumpSubDestinations[currByteIndex] = currByteValueBeginSub;
            }
        }
    }
}
