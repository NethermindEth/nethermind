using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis
{
    public record NGram(ulong ngram)
    {
        public const uint SIZE = 7;
        public const ulong NULL = 0;
        const byte STOP = (byte)Instruction.STOP;

        const ulong twogramBitMask = (255UL << 8) | 255UL;
        const ulong threegramBitMask = (255UL << 8 * 2) | twogramBitMask;
        const ulong fourgramBitMask = (255U << 8 * 3) | threegramBitMask;
        const ulong fivegramBitMask = (255UL << 8 * 4) | fourgramBitMask;
        const ulong sixgramBitMask = (255UL << 8 * 5) | fivegramBitMask;
        const ulong sevengramBitMask = (255UL << 8 * 6) | sixgramBitMask;
        public static ulong[] bitMasks = [255UL, twogramBitMask, threegramBitMask, fourgramBitMask, fivegramBitMask, sixgramBitMask, sevengramBitMask];
        public static ulong[] byteIndexes = { 255UL, 255UL << 8, 255UL << 16, 255UL << 24, 255UL << 32, 255UL << 40, 255UL << 48, 255UL << 56 };
        public static ulong[] byteIndexShifts = { 0, 8, 16, 24, 32, 40, 48, 56 };


        public NGram(Instruction[] instructions) : this(InstructionsToNGram(instructions))
        {
        }

        public NGram(byte[] instructions) : this(BytesToNGram(instructions))
        {
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



