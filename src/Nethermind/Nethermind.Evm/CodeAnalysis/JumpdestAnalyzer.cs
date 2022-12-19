// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;
using System.Collections;
using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class JumpdestAnalyzer : ICodeInfoAnalyzer
    {
        private byte[] MachineCode { get; set; }

        private BitArray? _validJumpDestinations;
        private BitArray? _validJumpSubDestinations;
        private IReleaseSpec _releaseSpec;
        public JumpdestAnalyzer(byte[] code, IReleaseSpec spec)
        {
            MachineCode = code;
            _releaseSpec = spec;
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
            _validJumpDestinations = new BitArray(MachineCode.Length);
            _validJumpSubDestinations = new BitArray(MachineCode.Length);

            byte push1 = (byte)Instruction.PUSH1;
            byte push32 = (byte)Instruction.PUSH32;

            byte rjump = (byte)Instruction.RJUMP;
            byte rjumpi = (byte)Instruction.RJUMPI;
            byte rjumpv = (byte)Instruction.RJUMPV;

            byte jumpdest = (byte)Instruction.JUMPDEST;

            int index = 0;
            while (index < MachineCode.Length)
            {
                byte instruction = MachineCode[index];

                // JUMPDEST
                if (instruction == jumpdest)
                {
                    _validJumpDestinations.Set(index, true);
                }
                // BEGINSUB
                else if (_releaseSpec.SubroutinesEnabled && instruction == rjump)
                {
                    _validJumpSubDestinations.Set(index, true);
                }

                // instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32
                if (instruction >= push1 && instruction <= push32)
                {
                    //index += instruction - Instruction.PUSH1 + 2;
                    index += instruction - 0x60 + 2;
                }
                else if (_releaseSpec.StaticRelativeJumpsEnabled && (instruction == rjump || instruction == rjumpi))
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
        }
    }
}
