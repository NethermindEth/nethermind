using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis.StatsAnalyzer
{
    public readonly struct NGrams : IEnumerable<ulong>
    {
        public readonly ulong ulong0;
        public const uint MAX_SIZE = 7;
        public const ulong NULL = 0;
        public const Instruction RESET = Instruction.STOP;

        const ulong twogramBitMask = (255UL << 8) | 255UL;
        const ulong threegramBitMask = (255UL << 8 * 2) | twogramBitMask;
        const ulong fourgramBitMask = (255U << 8 * 3) | threegramBitMask;
        const ulong fivegramBitMask = (255UL << 8 * 4) | fourgramBitMask;
        const ulong sixgramBitMask = (255UL << 8 * 5) | fivegramBitMask;
        const ulong sevengramBitMask = (255UL << 8 * 6) | sixgramBitMask;
        public static ulong[] bitMasks = [255UL, twogramBitMask, threegramBitMask, fourgramBitMask, fivegramBitMask, sixgramBitMask, sevengramBitMask];
        public static ulong[] byteIndexes = { 255UL, 255UL << 8, 255UL << 16, 255UL << 24, 255UL << 32, 255UL << 40, 255UL << 48, 255UL << 56 };
        public static ulong[] byteIndexShifts = { 0, 8, 16, 24, 32, 40, 48, 56 };


        public NGrams(Instruction[] instructions) : this(FromInstructions(instructions))
        {
        }

        public NGrams(ulong value = NGrams.NULL)
        {
            ulong0 = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NGrams ProcessInstructions(IEnumerable<Instruction> instructions, NGrams ngrams, Action<ulong> action)
        {
            foreach (Instruction instruction in instructions)
            {
                ngrams = ProcessOneInstruction(instruction, ngrams, action);
            }
            return ngrams;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NGrams ProcessOneInstruction(Instruction instruction, Action<ulong> action)
        {
            return ProcessOneInstruction(instruction, this, action);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NGrams ProcessOneInstruction(Instruction instruction, NGrams ngrams, Action<ulong> action)
        {
            ngrams = ngrams.ShiftAdd(instruction);
            foreach (ulong ngram in ngrams)
                action(ngram);
            return ngrams;
        }

        public NGrams ShiftAdd(Instruction instruction)
        {
            return new NGrams(ShiftAdd(ulong0, instruction));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftAdd(ulong ngram, Instruction instruction)
        {
            if (instruction == (byte)RESET) return 0;
            return (ngram << 8) | (byte)instruction;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NGrams GetCounts(Instruction[] executionOpCodes, Dictionary<ulong, ulong> counts, NGrams ngrams = new NGrams())
        {
            Action<ulong> CountNGrams = (ulong ngram) => { counts[ngram] = 1 + CollectionsMarshal.GetValueRefOrAddDefault(counts, ngram, out bool _); };
            return NGrams.ProcessInstructions(executionOpCodes, ngrams, CountNGrams);
        }

        public static byte[] ToBytes(ulong ngram)
        {
            byte[] instructions = new byte[MAX_SIZE];
            int i = 0;
            for (i = 0; i < instructions.Length; i++)
            {
                instructions[instructions.Length - 1 - i] = (byte)((ngram & byteIndexes[i]) >> (i * 8));
                if (instructions[instructions.Length - 1 - i] == (byte)RESET)
                {
                    break;
                }
            }
            return instructions[(instructions.Length - i)..instructions.Length];
        }

        public byte[] ToBytes()
        {
            return ToBytes(ulong0);
        }

        public static Instruction[] ToInstructions(ulong ngram)
        {
            return ToBytes(ngram).Select(i => (Instruction)i).ToArray();
        }

        public Instruction[] ToInstructions()
        {
            return ToInstructions(ulong0);
        }

        public static string ToString(ulong ngram)
        {
            string s = "";
            foreach (Instruction instruction in ToInstructions(ngram))
            {
                s += $"{instruction.ToString()}";
                s = s.PadRight(s.Length + 1);
            }
            return s.Trim();
        }

        public override string ToString()
        {
            return ToString(ulong0);
        }

        private static ulong FromInstructions(Instruction[] instructions)
        {

            if (instructions.Length > MAX_SIZE)
                throw new ArgumentException($"Instructions length {instructions.Length} given exceeds max length of {MAX_SIZE}");

            ulong _ngram = 0;
            foreach (Instruction instruction in instructions)
            {
                _ngram = ShiftAdd(_ngram, instruction);
            }
            return _ngram;
        }


        public IEnumerator<ulong> GetEnumerator()
        {
            for (int i = 1; i < MAX_SIZE; i++)
            {
                if (NGrams.byteIndexes[i - 1] < ulong0)
                {
                    yield return this.ulong0 & NGrams.bitMasks[i];
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

    }
}



