using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis
{
    public record NGram(ulong ngram) : IEnumerable<ulong>
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


        public NGram(Instruction[] instructions) : this(FromInstructions(instructions))
        {
        }

        public NGram(byte[] instructions) : this(FromBytes(instructions))
        {
        }


        public NGram AddByte(byte instruction)
        {
            return this with { ngram = AddByte(ngram, instruction) };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AddByte(ulong ngram, byte instruction)
        {
            if (instruction == STOP) return 0;
            return (ngram << 8) | instruction;
        }


        public static byte[] AsByte(ulong ngram)
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

        public byte[] AsByte()
        {
            return AsByte(ngram);
        }

        public static Instruction[] AsInstructions(ulong ngram)
        {
            return AsByte(ngram).Select(i => (Instruction)i).ToArray();
        }

        public Instruction[] AsInstructions()
        {
            return AsInstructions(ngram);
        }

        public static string AsString(ulong ngram)
        {
            string s = "";
            foreach (Instruction instruction in AsInstructions(ngram))
                s += $"{instruction.ToString()}".PadRight(1);
            return s;
        }

        public string AsString()
        {
            return AsString(ngram);
        }

        private static ulong FromInstructions(Instruction[] instructions)
        {

            byte[] byteArray = instructions.Select(i => (byte)i).ToArray();
            return FromBytes(byteArray);
        }

        private static ulong FromBytes(byte[] instructions)
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


        public IEnumerator<ulong> GetEnumerator()
        {
            for (int i = 1; i < NGram.SIZE; i++)
            {
                if (NGram.byteIndexes[i - 1] < ngram)
                {
                    yield return this.ngram & NGram.bitMasks[i]; // Corrected the syntax here
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

    }
}



