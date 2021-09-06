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
using System.Threading;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo
    {
        private ICodeInfoAnalyzer? _calculator;
        
        public byte[] MachineCode { get; set; }
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
            if (_calculator == null)
            {
                // Do sampling to choose an algo
                if (MachineCode.Length > 10_000)
                {
                    byte push1Count = 0;
                    
                    Random rand = new ();
                    for (int i = 0; i < 100; i++)
                    {
                        byte instruction = MachineCode[rand.Next(0, MachineCode.Length)];

                        // PUSH1
                        if (instruction == 0x60)
                        {
                            push1Count++;
                        }
                    }

                    // if there are many PUSH1 ops then use JUMPDEST analyzer
                    _calculator = push1Count > 40 ? new JumpdestAnalyzer(MachineCode) : new CodeDataAnalyzer(MachineCode);
                }
                else
                {
                    _calculator = new CodeDataAnalyzer(MachineCode);
                }
                
                return _calculator.ValidateJump(destination, isSubroutine);

            }
            else
            {
                return _calculator.ValidateJump(destination, isSubroutine);
            }
        }
    }
}
