// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Collections;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
        private IReleaseSpec _releaseSpec;
        private JumpAnalysisResult[] _analysisResults;

        public JumpdestAnalyzer(byte[] code, EofHeader? header, IReleaseSpec spec)
        {
            MachineCode = code;
            Header = header;
            _releaseSpec = spec;
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
                else if (_releaseSpec.SubroutinesEnabled && instruction == 0x5c)
                {
                    analysisResults._validJumpSubDestinations.Set(index, true);
                }

                // instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32
                if (instruction >= 0x60 && instruction <= 0x7f)
                {
                    //index += instruction - Instruction.PUSH1 + 2;
                    index += instruction - 0x60 + 2;
                }
                else if (_releaseSpec.StaticRelativeJumpsEnabled && instruction == 0x5c || instruction == 0x5d)
                {
                    index += 3;
                }
                else if (_releaseSpec.FunctionSections && (instruction == (byte)Instruction.CALLF || instruction == (byte)Instruction.JUMPF))
                {
                    index += 3;
                }
                else if (_releaseSpec.StaticRelativeJumpsEnabled && instruction == 0x5e)
                {
                    byte count = MachineCode[index + 1];
                    index += 2 + count * 2;
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
