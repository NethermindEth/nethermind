using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis
{
    public record NGram
    {
        public ulong ngram;
        const uint SIZE = 7;
        const byte STOP = (byte)Instruction.STOP;
        private ulong[] bitMasks { get; init; }
        private ulong[] byteIndexes { get; init; }
        private ulong[] byteIndexShifts { get; init; }

        public NGram(Instruction[] instructions) : this(InstructionsToNGram(instructions))
        {
        }

        public NGram(byte[] instructions) : this(BytesToNGram(instructions))
        {
        }

        public NGram(ulong ngram) : this(ngram, GetMasksIndexesAndShifts())
        {
        }

        private NGram(ulong ngram, (ulong[] bitMasks, ulong[] byteIndexes, ulong[] byteIndexShifts) masksIndexesAndShifts)
        {
            this.ngram = ngram;
            this.bitMasks = masksIndexesAndShifts.bitMasks;
            this.byteIndexes = masksIndexesAndShifts.byteIndexes;
            this.byteIndexShifts = masksIndexesAndShifts.byteIndexShifts;

        }

        public byte[] AsByte()
        {
            byte[] instructions = new byte[7];
            int i = 0;
            for (i = 0; i < instructions.Length; i++)
            {
                instructions[instructions.Length - 1 - i] = (byte)((ngram & byteIndexes[i]) >> (i * 8));
                if (instructions[instructions.Length - 1 - i] == STOP)
                {
                    break;
                }
            }
            return instructions[(instructions.Length - i)..instructions.Length];
        }

        public Instruction[] AsInstructions()
        {
            return AsByte().Select(i => (Instruction)i).ToArray();
        }

        public string AsString()
        {
            string s = "";
            foreach (Instruction instruction in AsInstructions())
                s += $"{instruction.ToString()}";
            return s;
        }


        private static (ulong[] bitMasks, ulong[] byteIndexes, ulong[] byteIndexShifts) GetMasksIndexesAndShifts(uint bytes = 64)
        {
            ulong[] bitMasks = new ulong[] { 255UL };
            ulong[] byteIndexes = new ulong[] { 255UL };
            ulong[] byteIndexShifts = new ulong[] { 0 };

            for (int i = 1; i < bytes / 8; i++)
            {
                byteIndexShifts[i] = 255UL << 8 * i;
                bitMasks[i] = byteIndexShifts[i] | bitMasks[i - 1];
                byteIndexShifts[i] = (ulong)i;

            }
            return (bitMasks, byteIndexes, byteIndexShifts);
        }


        private static ulong InstructionsToNGram(Instruction[] instructions)
        {

            byte[] byteArray = instructions.Select(i => (byte)i).ToArray();
            return BytesToNGram(byteArray);
        }

        private static ulong BytesToNGram(byte[] instructions)
        {
            if (instructions.Length > 7)
                throw new ArgumentException($"Invalid byte length found expected {SIZE}");

            ulong _ngram = 0;
            foreach (byte instruction in instructions)
            {
                _ngram = AddByte(_ngram, instruction);
            }
            return _ngram;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AddByte(ulong ngram, byte instruction)
        {
            return (ngram << 8) | instruction;
        }

    }
}



