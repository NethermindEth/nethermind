using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer
{
    public readonly struct NGram : IEquatable<NGram>
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


        public NGram(Instruction[] instructions) : this(FromInstructions(instructions))
        {
        }

        public NGram(ulong value = NGram.NULL)
        {
            ulong0 = value;
        }
         public override bool Equals(object? obj) => obj is NGram other && this.Equals(other);

        public bool Equals(NGram other) => ulong0 == other.ulong0;

        public override int GetHashCode() => ulong0.GetHashCode();

        public static bool operator ==(NGram lhs, NGram rhs) => lhs.Equals(rhs);

        public static bool operator !=(NGram lhs, NGram rhs) => !(lhs == rhs);

     //   [MethodImpl(MethodImplOptions.AggressiveInlining)]
     //   public static NGram ProcessInstructions(IEnumerable<Instruction> instructions, NGram Ngram, Action<ulong> action)
     //   {
     //       foreach (Instruction instruction in instructions)
     //       {
     //           Ngram = ProcessEachSubsequence(Ngram, action);
     //       }
     //       return Ngram;
     //   }

     //   [MethodImpl(MethodImplOptions.AggressiveInlining)]
     //   public NGram ProcessEachSubsequence(Instruction instruction, Action<ulong> action)
     //   {
     //       return ProcessEachSubsequence(this, action);
     //   }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NGram ProcessEachSubsequence(NGram ngrams, Action<NGram> action)
        {
            foreach (var ngram in ngrams.GetSubsequences())
                action(ngram);
            return ngrams;
        }

        public NGram ShiftAdd(Instruction instruction)
        {
            return new NGram(ShiftAdd(ulong0, instruction));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftAdd(ulong ngram, Instruction instruction)
        {
            if (instruction == (byte)RESET) return 0;
            return (ngram << 8) | (byte)instruction;
        }


     //   [MethodImpl(MethodImplOptions.AggressiveInlining)]
     //   public static NGram GetCounts(Instruction[] executionOpCodes, Dictionary<ulong, ulong> counts, NGram Ngram = new NGram())
     //   {
     //       Action<ulong> CountNGrams = (ulong ngram) => { counts[ngram] = 1 + CollectionsMarshal.GetValueRefOrAddDefault(counts, ngram, out bool _); };
     //       return NGram.ProcessInstructions(executionOpCodes, Ngram, CountNGrams);
     //   }

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

        public string[] ToArray()
        {
            return ToBytes(ulong0).Select(i => ((Instruction)i).ToString()).ToArray();
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


        public IEnumerable<NGram> GetSubsequences()
        {
            for (int i = 1; i < MAX_SIZE; i++)
            {
                if (NGram.byteIndexes[i - 1] < ulong0)
                {
                    yield return new NGram(this.ulong0 & NGram.bitMasks[i]);
                }

    }
        }


    }
}



