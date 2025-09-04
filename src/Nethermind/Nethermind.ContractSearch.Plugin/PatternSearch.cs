using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using System.Runtime.CompilerServices;

public static class PatternSearch
{

    const int WindowSizeAvx2 = 32;

    public static unsafe List<int> SemanticPatternSearch(ReadOnlySpan<byte> byteCode, ReadOnlySpan<byte> pattern)
    {
        //todo;
        List<int> matchIndices = [];
        return matchIndices;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Compare(ReadOnlySpan<byte> byteCode, int start, ReadOnlySpan<byte> pattern)
    {
        return byteCode.Slice(start, pattern.Length).SequenceEqual(pattern);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CheckSyntax(ValueHash256 codeHash, ReadOnlySpan<byte> pattern, int pos, OpcodeIndexer opcodeIndexer)
    {
        return opcodeIndexer.Get(codeHash, (uint)pos) == (Instruction)pattern[0];
    }


    public static unsafe List<int> SyntacticPatternSearch(ValueHash256 codeHash, ReadOnlySpan<byte> byteCode, ReadOnlySpan<byte> pattern, OpcodeIndexer opcodeIndexer)
    {
        int codeLength = byteCode.Length;
        int patternLength = pattern.Length;
        List<int> matchIndices = [];

        if (patternLength == 0 || codeLength < patternLength)
        {
            return matchIndices;
        }

        byte first = pattern[0];
        byte last = pattern[patternLength - 1];

        int[] skipTable = new int[256];

        for (int i = 0; i < 256; i++) skipTable[i] = patternLength;
        for (int i = 0; i < patternLength - 1; i++)
        {
            skipTable[pattern[i]] = patternLength - i - 1;

        }

        fixed (byte* ptr = byteCode)
        {
            int currentPos = 0;
            int end = codeLength - patternLength;

            if (Avx2.IsSupported)
            {

                Vector256<byte> lastVec = Vector256.Create(last);

                while (currentPos <= end - WindowSizeAvx2)
                {

                    Vector256<byte> block = Avx.LoadVector256(ptr + currentPos + patternLength - 1);
                    Vector256<byte> cmp = Avx2.CompareEqual(block, lastVec);
                    int mask = Avx2.MoveMask(cmp);
                    while (mask != 0)
                    {
                        int offset = BitOperations.TrailingZeroCount(mask);
                        int pos = currentPos + offset;

                        if (pos + patternLength > codeLength)
                        {
                            break;
                        }

                        bool match = Compare(byteCode, pos, pattern);

                        if (match && CheckSyntax(codeHash, pattern, pos, opcodeIndexer))
                        {
                            matchIndices.Add(pos);
                        }

                        mask &= ~(1 << offset);
                    }

                    currentPos += WindowSizeAvx2;
                }
            }

            while (currentPos <= end)
            {
                byte b = byteCode[currentPos];

                if (b == first)
                {
                    bool match = Compare(byteCode, currentPos, pattern);

                    if (match && CheckSyntax(codeHash, pattern, currentPos, opcodeIndexer))
                    {
                        matchIndices.Add(currentPos);
                    }
                }

                //int skip = skipTable[b];

                currentPos += 1;
            }
        }

        return matchIndices;
    }
}


