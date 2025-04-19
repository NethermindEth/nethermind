using FE = Nethermind.Verkle.Fields.FpEElement.FpE;

namespace Nethermind.Verkle.Fields.FpEElement;

public static class LookUpTable
{
    private const ulong RU0 = 17662759726869145328;
    private const ulong RU1 = 5064093798700299342;
    private const ulong RU2 = 10063401387408601010;
    private const ulong RU3 = 3873438938108290097;

    private const ulong g24_0 = 3426179166498458371;
    private const ulong g24_1 = 17774891265778865780;
    private const ulong g24_2 = 289456567831301218;
    private const ulong g24_3 = 1946194709224543838;

    private const ulong g16_0 = 9256342164997675388;
    private const ulong g16_1 = 9412477108735699039;
    private const ulong g16_2 = 18002130140546731823;
    private const ulong g16_3 = 7306109011983698537;

    private const ulong g8_0 = 17088207400005424367;
    private const ulong g8_1 = 2154423591275905172;
    private const ulong g8_2 = 16679520095305972919;
    private const ulong g8_3 = 1422677820602253280;

    private const ulong gInv_0 = 16202019587029671427;
    private const ulong gInv_1 = 12017219221170101976;
    private const ulong gInv_2 = 7842944655549890123;
    private const ulong gInv_3 = 1422657761923336254;

    private const ulong gInv8_0 = 15118332175617490275;
    private const ulong gInv8_1 = 6984912174216846004;
    private const ulong gInv8_2 = 17331013002699678324;
    private const ulong gInv8_3 = 7916174170450263136;

    private const ulong gInv16_0 = 16405696411196864266;
    private const ulong gInv16_1 = 2972828062941467037;
    private const ulong gInv16_2 = 3105686916971696149;
    private const ulong gInv16_3 = 6689846928035436105;

    private const ulong gInv24_0 = 13332407645450386590;
    private const ulong gInv24_1 = 16711928638495282130;
    private const ulong gInv24_2 = 6785075770842394458;
    private const ulong gInv24_3 = 7173692049422626809;

    // g is 2^32 th primitive root. it is denoted by dyadicRootOfUnity.
    public static readonly FE dyadicRootOfUnity = new(RU0, RU1, RU2, RU3);

    //g^(2^24)
    private static readonly FE g24 = new(g24_0, g24_1, g24_2, g24_3);

    //g^(2^16)
    public static readonly FE g16 = new(g16_0, g16_1, g16_2, g16_3);

    //g^(2^8)
    public static readonly FE g8 = new(g8_0, g8_1, g8_2, g8_3);

    //inverse of dyadicRootOfUnity, that is inverse of 2^32 th primitive root.
    private static readonly FE gInv = new(gInv_0, gInv_1, gInv_2, gInv_3);

    private static readonly FE gInv8 = new(gInv8_0, gInv8_1, gInv8_2, gInv8_3);

    private static readonly FE gInv16 = new(gInv16_0, gInv16_1, gInv16_2, gInv16_3);

    private static readonly FE gInv24 = new(gInv24_0, gInv24_1, gInv24_2, gInv24_3);

    public static readonly FE[,] _table;
    public static readonly Dictionary<FE, byte> mapG2_24;

    static LookUpTable()
    {
        _table = GenerateLookUpTable();
        mapG2_24 = LookUpTableG2_24();
    }

    private static FE[,] GenerateLookUpTable()
    {
        FE[,] table = new FE[4, 256];
        FE[] gValues = [gInv, gInv8, gInv16, gInv24];
        Parallel.For(0, 4, i =>
        {
            table[i, 0] = FE.One;
            for (int j = 1; j < 256; j++) FE.MultiplyMod(gValues[i], table[i, j - 1], out table[i, j]);
        });

        return table;
    }

    private static Dictionary<FE, byte> LookUpTableG2_24()
    {
        Dictionary<FE, byte> map = new();
        FE res = FE.One;
        byte i = 0;
        for (; i < 255; i++)
        {
            map[res] = i;
            FE.MultiplyMod(res, g24, out res);
        }

        map[res] = i;
        return map;
    }
}

public readonly partial struct FpE
{
    private static FE ComputeRelevantPowers(in FE z, out FE squareRootCandidate, out FE rootOfUnity)
    {
        void SquareEqNTimes(FE x, out FE y, int n)
        {
            for (int i = 0; i < n; i++) MultiplyMod(in x, in x, out x);
            y = x;
        }

        FE z2 = new();
        FE z3 = new();
        FE z7 = new();
        FE z6 = new();
        FE z9 = new();
        FE z11 = new();
        FE z13 = new();
        FE z19 = new();
        FE z21 = new();
        FE z25 = new();
        FE z27 = new();
        FE z29 = new();
        FE z31 = new();
        FE z255 = new();
        FE acc = new();

        MultiplyMod(in z, in z, out z2); // 0b10
        MultiplyMod(in z, in z2, out z3); // 0b11
        MultiplyMod(in z3, in z3, out z6); // 0b110
        MultiplyMod(in z, in z6, out z7);
        MultiplyMod(in z7, in z2, out z9);
        MultiplyMod(in z9, in z2, out z11);
        MultiplyMod(in z11, in z2, out z13);
        MultiplyMod(in z13, in z6, out z19);
        MultiplyMod(in z2, in z19, out z21);
        MultiplyMod(in z19, in z6, out z25);
        MultiplyMod(in z25, in z2, out z27);
        MultiplyMod(in z27, in z2, out z29);
        MultiplyMod(in z29, in z2, out z31);
        MultiplyMod(in z27, in z29, out acc); //56
        MultiplyMod(in acc, in acc, out acc); //112
        MultiplyMod(in acc, in acc, out acc); //224
        MultiplyMod(in acc, in z31, out z255); //255
        MultiplyMod(in acc, in acc, out acc); //448
        MultiplyMod(in acc, in acc, out acc); //896
        MultiplyMod(in acc, in z31, out acc); //927
        SquareEqNTimes(acc, out acc, 6); //59328
        MultiplyMod(in acc, in z27, out acc); //59355
        SquareEqNTimes(acc, out acc, 6); //3798720
        MultiplyMod(in acc, in z19, out acc); //3798739
        SquareEqNTimes(acc, out acc, 5); //121559648
        MultiplyMod(in acc, in z21, out acc); //121559669
        SquareEqNTimes(acc, out acc, 7);
        MultiplyMod(in acc, in z25, out acc);
        SquareEqNTimes(acc, out acc, 6);
        MultiplyMod(in acc, in z19, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z7, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z11, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z29, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z9, out acc);
        SquareEqNTimes(acc, out acc, 7);
        MultiplyMod(in acc, in z3, out acc);
        SquareEqNTimes(acc, out acc, 7);
        MultiplyMod(in acc, in z25, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z25, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z27, out acc);
        SquareEqNTimes(acc, out acc, 8);
        MultiplyMod(in acc, in z, out acc);
        SquareEqNTimes(acc, out acc, 8);
        MultiplyMod(in acc, in z, out acc);
        SquareEqNTimes(acc, out acc, 6);
        MultiplyMod(in acc, in z13, out acc);
        SquareEqNTimes(acc, out acc, 7);
        MultiplyMod(in acc, in z7, out acc);
        SquareEqNTimes(acc, out acc, 3);
        MultiplyMod(in acc, in z3, out acc);
        SquareEqNTimes(acc, out acc, 13);
        MultiplyMod(in acc, in z21, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z9, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z27, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z27, out acc);
        SquareEqNTimes(acc, out acc, 5);
        MultiplyMod(in acc, in z9, out acc);
        SquareEqNTimes(acc, out acc, 10);
        MultiplyMod(in acc, in z, out acc);
        SquareEqNTimes(acc, out acc, 7);
        MultiplyMod(in acc, in z255, out acc);
        SquareEqNTimes(acc, out acc, 8);
        MultiplyMod(in acc, in z255, out acc);
        SquareEqNTimes(acc, out acc, 6);
        MultiplyMod(in acc, in z11, out acc);
        SquareEqNTimes(acc, out acc, 9);
        MultiplyMod(in acc, in z255, out acc);
        SquareEqNTimes(acc, out acc, 2);
        MultiplyMod(in acc, in z, out acc);
        SquareEqNTimes(acc, out acc, 7);
        MultiplyMod(in acc, in z255, out acc);
        SquareEqNTimes(acc, out acc, 8);
        MultiplyMod(in acc, in z255, out acc);
        SquareEqNTimes(acc, out acc, 8);
        MultiplyMod(in acc, in z255, out acc);
        SquareEqNTimes(acc, out acc, 8);
        MultiplyMod(in acc, in z255, out acc);
        // acc = n^(Q-1)/2
        MultiplyMod(in acc, in acc, out rootOfUnity);
        MultiplyMod(in rootOfUnity, in z, out rootOfUnity); // n^Q
        MultiplyMod(in acc, in z, out squareRootCandidate); // n^(Q+1)/2

        return acc;
    }

    // Given n, whose square root needs to be found, this method returns n^(Q*2^24), n^(Q*2^16), n^(Q*2^8), n^(Q) where p-1 = Q*2^s where s is 32 for this curve. This method takes n^Q as argument.
    private static void PowersOfNq(in FE n, in Span<FE> arr)
    {
        FE res = n;
        arr[0] = res;

        for (int i = 0; i < 24; i++)
        {
            MultiplyMod(res, res, out res);
            switch (i)
            {
                case 7:
                    arr[1] = res;
                    break;
                case 15:
                    arr[2] = res;
                    break;
            }
        }

        arr[3] = res;
    }

    // Given a number x, this method returns y0, y1, y2, y3: where x = (2^24)*y0 + (2^16)*y1 + (2^8)*y2 + (2^0)*y3
    private static void DecomposeNumber(int x, in Span<byte> arr)
    {
        arr[0] = (byte)((x >> 24) & 0xFF); // Shift right by 24 bits and mask out the last 8 bits
        arr[1] = (byte)((x >> 16) & 0xFF); // Shift right by 16 bits and mask out the last 8 bits
        arr[2] = (byte)((x >> 8) & 0xFF); // Shift right by 8 bits and mask out the last 8 bits
        arr[3] = (byte)(x & 0xFF); // Mask out the last 8 bits
    }

    // this method decomposes x as defined above and then calculates value for each y0. y1, y2, y3 using the lookup table
    private static void ComputePower(int x, FE[,] table, out FE res)
    {
        Span<byte> arr = stackalloc byte[4];
        DecomposeNumber(x, in arr);
        MultiplyMod(table[3, arr[0]], table[2, arr[1]], out res);
        MultiplyMod(res, table[1, arr[2]], out res);
        MultiplyMod(res, table[0, arr[3]], out res);
    }

    // implementing the main algo
    public static bool Sqrt(in FE n, out FE sqrt)
    {
        ComputeRelevantPowers(n, out FE squareRootCandidate, out FE rootOfUnity);

        Span<FE> arrOfN = stackalloc FE[4];
        PowersOfNq(rootOfUnity, in arrOfN);

        byte x0, x1, x2, x3;

        FE[,] table = LookUpTable._table;
        Dictionary<FE, byte> mapG2_24 = LookUpTable.mapG2_24;

        x3 = mapG2_24[arrOfN[3]];

        // If x3 is odd, then there is no square root.
        if (x3 % 2 == 1)
        {
            sqrt = Zero;
            return false;
        }

        MultiplyMod(arrOfN[2], table[2, x3], out FE secEq);

        x2 = mapG2_24[secEq];
        MultiplyMod(arrOfN[1], table[2, x2], out FE thirdEq);
        MultiplyMod(thirdEq, table[1, x3], out thirdEq);

        x1 = mapG2_24[thirdEq];
        MultiplyMod(arrOfN[0], table[2, x1], out FE fourthEq);
        MultiplyMod(fourthEq, table[1, x2], out fourthEq);
        MultiplyMod(fourthEq, table[0, x3], out fourthEq);

        x0 = mapG2_24[fourthEq];

        int xBy2 = ((1 << 23) * x0) + ((1 << 15) * x1) + ((1 << 7) * x2) + (x3 / 2);


        ComputePower(xBy2, table, out sqrt);

        MultiplyMod(sqrt, squareRootCandidate, out sqrt);

        return true;
    }
}
