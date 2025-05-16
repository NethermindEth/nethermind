using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Numerics;

public static class PatternSearch
{

    const int WindowSizeAvx2 = 32;

    public static unsafe List<int> SemanticPatternSearch(ReadOnlySpan<byte> byteCode, ReadOnlySpan<byte> pattern)
    {
        //todo;
        List<int> matchIndices = [];
        return matchIndices;
    }

    public static unsafe List<int> SyntacticPatternSearch(ReadOnlySpan<byte> byteCode, ReadOnlySpan<byte> pattern)
    {
        int codeLength = byteCode.Length;
        int patternLength = pattern.Length;
        List<int> matchIndices = [];

        if (patternLength == 0 || codeLength < patternLength)
        {

            return matchIndices;
        }

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

            if (Avx2.IsSupported && patternLength >= WindowSizeAvx2)
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

                        bool match = true;
                        for (int j = 0; j < patternLength; j++)
                        {
                            byte h = byteCode[pos + j];
                            byte n = pattern[j];

                            if (h != n)
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
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
                byte b = byteCode[currentPos + patternLength - 1];

                if (b == last)
                {

                    bool match = true;
                    for (int j = 0; j < patternLength; j++)
                    {
                        byte h = byteCode[currentPos + j];
                        byte n = pattern[j];

                        if (h != n)
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        matchIndices.Add(currentPos);
                    }
                }

                int skip = skipTable[b];

                currentPos += skip;
            }
        }

        return matchIndices;
    }
}


