using System.Runtime.CompilerServices;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;

public readonly struct NGram : IEquatable<NGram>
{
    public readonly ulong Ulong0;
    public const uint MaxSize = 7;
    public const ulong Null = 0;
    public const Instruction Reset = Instruction.STOP;

    private const ulong TwogramBitMask = (255UL << 8) | 255UL;
    private const ulong ThreegramBitMask = (255UL << (8 * 2)) | TwogramBitMask;
    private const ulong FourgramBitMask = (255U << (8 * 3)) | ThreegramBitMask;
    private const ulong FivegramBitMask = (255UL << (8 * 4)) | FourgramBitMask;
    private const ulong SixgramBitMask = (255UL << (8 * 5)) | FivegramBitMask;
    private const ulong SevengramBitMask = (255UL << (8 * 6)) | SixgramBitMask;

    public static ulong[] bitMasks =
    [
        255UL, TwogramBitMask, ThreegramBitMask, FourgramBitMask, FivegramBitMask, SixgramBitMask, SevengramBitMask
    ];

    public static ulong[] byteIndexes =
        { 255UL, 255UL << 8, 255UL << 16, 255UL << 24, 255UL << 32, 255UL << 40, 255UL << 48, 255UL << 56 };

    public static ulong[] byteIndexShifts = { 0, 8, 16, 24, 32, 40, 48, 56 };


    public NGram(Instruction[] instructions) : this(FromInstructions(instructions))
    {
    }

    public NGram(ulong value = Null)
    {
        Ulong0 = value;
    }

    public override bool Equals(object? obj)
    {
        return obj is NGram other && Equals(other);
    }

    public bool Equals(NGram other)
    {
        return Ulong0 == other.Ulong0;
    }

    public override int GetHashCode()
    {
        return Ulong0.GetHashCode();
    }

    public static bool operator ==(NGram lhs, NGram rhs)
    {
        return lhs.Equals(rhs);
    }

    public static bool operator !=(NGram lhs, NGram rhs)
    {
        return !(lhs == rhs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ulong ProcessEachSubsequence(NGram ngram,
        delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>, PriorityQueue<ulong, ulong>,
            ulong> action, int currentSketchPos,
        int bufferSize, ulong minSupport, ulong max, int topN, CmSketch[] sketchBuffer,
        Dictionary<ulong, ulong> topNMap, PriorityQueue<ulong, ulong> topNQueue)

    {
        for (var i = 1; i < MaxSize; i++)
            if (byteIndexes[i - 1] < ngram.Ulong0)
                max = action(ngram.Ulong0 & bitMasks[i], currentSketchPos, bufferSize, minSupport, max, topN,
                    sketchBuffer, topNMap, topNQueue);
        return max;
    }

    public NGram ShiftAdd(Instruction instruction)
    {
        return new NGram(ShiftAdd(Ulong0, instruction));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ShiftAdd(ulong ngram, Instruction instruction)
    {
        if (instruction == (byte)Reset) return 0;
        return (ngram << 8) | (byte)instruction;
    }

    public static byte[] ToBytes(ulong ngram)
    {
        var instructions = new byte[MaxSize];
        var i = 0;
        for (i = 0; i < instructions.Length; i++)
        {
            instructions[instructions.Length - 1 - i] = (byte)((ngram & byteIndexes[i]) >> (i * 8));
            if (instructions[instructions.Length - 1 - i] == (byte)Reset) break;
        }

        return instructions[(instructions.Length - i)..instructions.Length];
    }

    public byte[] ToBytes()
    {
        return ToBytes(Ulong0);
    }

    public string[] ToArray()
    {
        return ToBytes(Ulong0).Select(i => ((Instruction)i).ToString()).ToArray();
    }

    public static Instruction[] ToInstructions(ulong ngram)
    {
        return ToBytes(ngram).Select(i => (Instruction)i).ToArray();
    }

    public Instruction[] ToInstructions()
    {
        return ToInstructions(Ulong0);
    }

    public static string ToString(ulong ngram)
    {
        var s = "";
        foreach (var instruction in ToInstructions(ngram))
        {
            s += $"{instruction.ToString()}";
            s = s.PadRight(s.Length + 1);
        }

        return s.Trim();
    }

    public override string ToString()
    {
        return ToString(Ulong0);
    }

    private static ulong FromInstructions(Instruction[] instructions)
    {
        if (instructions.Length > MaxSize)
            throw new ArgumentException(
                $"Instructions length {instructions.Length} given exceeds max length of {MaxSize}");

        ulong ngram = 0;
        foreach (var instruction in instructions) ngram = ShiftAdd(ngram, instruction);

        return ngram;
    }


    public IEnumerable<NGram> GetSubsequences()
    {
        for (var i = 1; i < MaxSize; i++)
            if (byteIndexes[i - 1] < Ulong0)
                yield return new NGram(Ulong0 & bitMasks[i]);
    }
}
