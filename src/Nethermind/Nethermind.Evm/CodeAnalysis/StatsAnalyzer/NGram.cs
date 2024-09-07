using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{
    public readonly struct NGram : IEnumerable<ulong>
    {
        public readonly ulong ngram;
        public const uint MAX_SIZE = 7;
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

        public NGram(ulong value)
        {
            ngram = value;
        }


        public NGram ShiftAdd(Instruction instruction)
        {
            return new NGram(ShiftAdd(ngram, instruction));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftAdd(ulong ngram, Instruction instruction)
        {
            if (instruction == STOP) return 0;
            return (ngram << 8) | (byte)instruction;
        }


        public static byte[] ToBytes(ulong ngram)
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

        public byte[] ToBytes()
        {
            return ToBytes(ngram);
        }

        public static Instruction[] ToInstructions(ulong ngram)
        {
            return ToBytes(ngram).Select(i => (Instruction)i).ToArray();
        }

        public Instruction[] ToInstructions()
        {
            return ToInstructions(ngram);
        }

        public static string ToString(ulong ngram)
        {
            string s = "";
            foreach (Instruction instruction in ToInstructions(ngram))
                s += $"{instruction.ToString()}".PadRight(1);
            return s;
        }

        public override string ToString()
        {
            return ToString(ngram);
        }

        private static ulong FromInstructions(Instruction[] instructions)
        {

            if (instructions.Length > 7)
                throw new ArgumentException($"Invalid byte length found expected {MAX_SIZE}");

            ulong _ngram = 0;
            foreach (Instruction instruction in instructions)
            {
                _ngram = ShiftAdd(_ngram, instruction);
            }
            return _ngram;
        }


        public IEnumerator<ulong> GetEnumerator()
        {
            for (int i = 1; i < NGram.MAX_SIZE; i++)
            {
                if (NGram.byteIndexes[i - 1] < ngram)
                {
                    yield return this.ngram & NGram.bitMasks[i];
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

    }
}



