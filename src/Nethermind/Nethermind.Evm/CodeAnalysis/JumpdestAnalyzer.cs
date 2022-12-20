// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Collections;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;
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
            _analysisResults = new JumpAnalysisResult[Header?.CodeSections.ChildSections.Length ?? 1];
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

        private void CalculateJumpDestinations(int sectionId = 0)
        {
            (var sectionStart, var sectionSize) = (0, MachineCode.Length);
            if (Header is not null)
            {
                sectionStart = Header.Value.CodeSections[sectionId].Start;
                sectionSize = Header.Value.CodeSections[sectionId].Size;
            }
            var codeSection = MachineCode.Slice(sectionStart, sectionSize);

            var analysisResults = new JumpAnalysisResult
            {
                _validJumpDestinations = new BitArray(codeSection.Length),
                _validJumpSubDestinations = new BitArray(codeSection.Length)
            };

            byte push1 = (byte)Instruction.PUSH1;
            byte push32 = (byte)Instruction.PUSH32;

            byte rjump = (byte)Instruction.RJUMP;
            byte rjumpi = (byte)Instruction.RJUMPI;
            byte rjumpv = (byte)Instruction.RJUMPV;

            byte jumpdest = (byte)Instruction.JUMPDEST;

            byte callf = (byte)Instruction.CALLF;
            byte jumpf = (byte)Instruction.JUMPF;

            int index = 0;
            while (index < codeSection.Length)
            {
                byte instruction = codeSection[index];

                // JUMPDEST
                if (instruction == jumpdest)
                {
                    analysisResults._validJumpDestinations.Set(index, true);
                }
                // BEGINSUB
                else if (_releaseSpec.SubroutinesEnabled && instruction == rjump)
                {
                    analysisResults._validJumpSubDestinations.Set(index, true);
                }

                // instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32
                if (instruction >= push1 && instruction <= push32)
                {
                    //index += instruction - Instruction.PUSH1 + 2;
                    index += instruction - 0x60 + 2;
                }
                else if (_releaseSpec.StaticRelativeJumpsEnabled && instruction == rjump || instruction == rjumpi)
                {
                    index += 3;
                }
                else if (_releaseSpec.FunctionSections && (instruction == callf || instruction == jumpf))
                {
                    index += 3;
                }
                else if (_releaseSpec.StaticRelativeJumpsEnabled && instruction == rjumpv)
                {
                    byte count = MachineCode[index + 1];
                    index += 2 + count * 2;
                }
                else
                {
                    index++;
                }
            }
            _analysisResults[sectionId] = analysisResults;
        }
    }
}
