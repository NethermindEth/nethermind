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
// 

using System;
using System.Collections;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class JumpdestAnalyzer : ICodeInfoAnalyzer
    {
        internal class JumpAnalysisResult
        {
            public BitArray? _validJumpDestinations;
            public BitArray? _validJumpSubDestinations;
        }
        private byte[] MachineCode { get; set; }
        private EofHeader? Header { get; set; }

        private JumpAnalysisResult[] _analysisResults;

        public JumpdestAnalyzer(byte[] code, EofHeader? header)
        {
            MachineCode = code;
            Header = header;
            _analysisResults = new JumpAnalysisResult[Header?.CodeSize?.Length ?? 1];
        }

        public bool ValidateJump(int destination, bool isSubroutine, int codeSectionId = 0)
        {
            if (_analysisResults[codeSectionId] is null)
            {
                CalculateJumpDestinations(codeSectionId);
            }

            var codeSectionResults = _analysisResults[codeSectionId];
            var validJumpDestinations = codeSectionResults._validJumpDestinations;
            var validJumpSubDestinations = codeSectionResults._validJumpSubDestinations;

            if (destination < 0 || destination >= validJumpDestinations.Length ||
                (isSubroutine ? !validJumpSubDestinations.Get(destination) : !validJumpDestinations.Get(destination)))
            {
                return false;
            }

            return true;
        }

        private void CalculateJumpDestinations(int codeSectionId = 0)
        {
            (var sectionStart, var SectionSize) = Header is null ? (0, MachineCode.Length) : Header[codeSectionId];
            var codeSection = MachineCode.Slice(sectionStart, SectionSize);
            var analysisResults = new JumpAnalysisResult
            {
                _validJumpDestinations = new BitArray(codeSection.Length),
                _validJumpSubDestinations = new BitArray(codeSection.Length)
            };
            
            int index = 0;
            while (index < codeSection.Length)
            {
                byte instruction = codeSection[index];

                // JUMPDEST
                if (instruction == 0x5b)
                {
                    analysisResults._validJumpDestinations.Set(index, true);
                }
                // BEGINSUB
                else if (instruction == 0x5c)
                {
                    analysisResults._validJumpSubDestinations.Set(index, true);
                }

                // instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32
                if (instruction >= 0x60 && instruction <= 0x7f)
                {
                    //index += instruction - Instruction.PUSH1 + 2;
                    index += instruction - 0x60 + 2;
                }
                else if (instruction == (byte)Instruction.CALLF || instruction == 0x5c || instruction == 0x5d) 
                {
                    index += 3;
                }
                else
                {
                    index++;
                }
            }
            _analysisResults[codeSectionId] = analysisResults;
        }
    }
}
