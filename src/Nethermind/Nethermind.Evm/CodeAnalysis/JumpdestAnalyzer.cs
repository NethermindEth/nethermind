// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;

namespace Nethermind.Evm.CodeAnalysis
{
    public class JumpdestAnalyzer : ICodeInfoAnalyzer
    {
        private ReadOnlyMemory<byte> Code { get; set; }

        private BitArray? _validJumpDestinations;
        private BitArray? _validJumpSubDestinations;

        public JumpdestAnalyzer(ReadOnlyMemory<byte> code)
        {
            Code = code;
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_validJumpDestinations is null)
            {
                CalculateJumpDestinations();
            }

            if (destination < 0 || destination >= _validJumpDestinations.Length ||
                (isSubroutine ? !_validJumpSubDestinations.Get(destination) : !_validJumpDestinations.Get(destination)))
            {
                return false;
            }

            return true;
        }

        private void CalculateJumpDestinations()
        {
            _validJumpDestinations = new BitArray(Code.Length);
            _validJumpSubDestinations = new BitArray(Code.Length);

            ReadOnlySpan<byte> code = Code.Span;
            int index = 0;
            while (index < code.Length)
            {
                byte instruction = code[index];

                // JUMPDEST
                if (instruction == 0x5b)
                {
                    _validJumpDestinations.Set(index, true);
                }
                // BEGINSUB
                else if (instruction == 0x5c)
                {
                    _validJumpSubDestinations.Set(index, true);
                }

                // instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32
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
