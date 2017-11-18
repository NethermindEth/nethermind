using System;

namespace Nevermind.Evm
{
    public class InvalidInstructionException : EvmException
    {
        public byte Instruction1 { get; }

        public InvalidInstructionException(byte instruction)
        {
            Instruction1 = instruction;
        }
    }
}