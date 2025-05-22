using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Evm;

namespace Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;

public readonly struct NGram(ulong value = NGram.Null) : IEquatable<NGram>
{
    public readonly ulong Ulong0 = value;
    private const uint MaxSize = 7;
    public const ulong Null = 0;
    public const Instruction Reset = Instruction.STOP;

    private const ulong TwoGramBitMask = (255UL << 8) | 255UL;
    private const ulong ThreeGramBitMask = (255UL << (8 * 2)) | TwoGramBitMask;
    private const ulong FourGramBitMask = (255U << (8 * 3)) | ThreeGramBitMask;
    private const ulong FiveGramBitMask = (255UL << (8 * 4)) | FourGramBitMask;
    private const ulong SixGramBitMask = (255UL << (8 * 5)) | FiveGramBitMask;
    private const ulong SevenGramBitMask = (255UL << (8 * 6)) | SixGramBitMask;

    private static readonly ulong[] BitMasks =
    [
        255UL, TwoGramBitMask, ThreeGramBitMask, FourGramBitMask, FiveGramBitMask, SixGramBitMask, SevenGramBitMask
    ];

    private static readonly ulong[] ByteIndexes =
        { 255UL, 255UL << 8, 255UL << 16, 255UL << 24, 255UL << 32, 255UL << 40, 255UL << 48, 255UL << 56 };


    public NGram(Instruction[] instructions) : this(FromInstructions(instructions))
    {
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
    public static unsafe ulong ProcessEachSubsequence(
        NGram ngram,
        delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>, PriorityQueue<ulong, ulong>, ulong> action,
        int currentSketchPos,
        int bufferSize,
        ulong minSupport,
        ulong max,
        int topN,
        CmSketch[] sketchBuffer,
        Dictionary<ulong, ulong> topNMap,
        PriorityQueue<ulong, ulong> topNQueue)

    {
        for (var i = 1; i < MaxSize; i++)
            if (ByteIndexes[i - 1] < ngram.Ulong0)
                max = action(
                        ngram.Ulong0 & BitMasks[i],
                        currentSketchPos,
                        bufferSize,
                        minSupport,
                        max,
                        topN,
                        sketchBuffer,
                        topNMap,
                        topNQueue);
        return max;
    }

    public NGram ShiftAdd(Instruction instruction)
    {
        return new NGram(ShiftAdd(Ulong0, instruction));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ShiftAdd(ulong ngram, Instruction instruction)
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
            instructions[instructions.Length - 1 - i] = (byte)((ngram & ByteIndexes[i]) >> (i * 8));
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
        var stringBuilder = new StringBuilder();
        foreach (var instruction in ToInstructions(ngram))
        {
            stringBuilder.Append(instruction.ToString());
            stringBuilder.Append(" ");
        }

        return stringBuilder.ToString().TrimEnd();
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
            if (ByteIndexes[i - 1] < Ulong0)
                yield return new NGram(Ulong0 & BitMasks[i]);
    }
}
