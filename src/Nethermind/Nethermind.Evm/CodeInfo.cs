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
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm
{
    public class CodeInfo
    {
        private BitArray _validJumpDestinations;
        private BitArray _validJumpSubDestinations;

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
        
        public byte[] MachineCode { get; set; }
        public IPrecompile Precompile { get; set; }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_validJumpDestinations is null)
            {
                CalculateJumpDestinations();
            }

            if (destination < 0 || destination >= MachineCode.Length ||
                (isSubroutine ? !_validJumpSubDestinations.Get(destination) : !_validJumpDestinations.Get(destination)))
            {
                return false;
            }

            return true;
        }
      
        private void CalculateJumpDestinations()
        {
            _validJumpDestinations = new BitArray(MachineCode.Length);
            _validJumpSubDestinations = new BitArray(MachineCode.Length);
            
            int index = 0;
            while (index < MachineCode.Length)
            {
                //Instruction instruction = (Instruction)code[index];
                byte instruction = MachineCode[index];                
                
                //if (instruction == Instruction.JUMPDEST
                if (instruction == 0x5b)
                {
                    _validJumpDestinations.Set(index, true);
                }
                //if (instruction == Instruction.BEGINSUB
                else if (instruction == 0x5c)
                {
                    _validJumpSubDestinations.Set(index, true);
                }
                //if (instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32)
                
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
        }
    }
}
