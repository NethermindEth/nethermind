using Nethermind.Int256;
using FE = Nethermind.Verkle.Fields.FpEElement.FpE;

namespace Nethermind.Verkle.Fields.FpEElement;

public readonly partial struct FpE
{
    private const int Limbs = 4;
    private const int Bits = 255;
    private const int Bytes = Limbs * 8;
    private const ulong SqrtR = 32;
    private const ulong QInvNeg = 18446744069414584319;

    public static readonly FE Zero = new(0);

    private const ulong One0 = 8589934590;
    private const ulong One1 = 6378425256633387010;
    private const ulong One2 = 11064306276430008309;
    private const ulong One3 = 1739710354780652911;
    public static readonly FE One = new(One0, One1, One2, One3);

    private const ulong Q0 = 18446744069414584321;
    private const ulong Q1 = 6034159408538082302;
    private const ulong Q2 = 3691218898639771653;
    private const ulong Q3 = 8353516859464449352;
    public static readonly FE qElement = new(Q0, Q1, Q2, Q3);

    private const ulong R0 = 14526898881837571181;
    private const ulong R1 = 3129137299524312099;
    private const ulong R2 = 419701826671360399;
    private const ulong R3 = 524908885293268753;
    private static readonly FE rSquare = new(R0, R1, R2, R3);

    private const ulong G0 = 11289237133041595516;
    private const ulong G1 = 2081200955273736677;
    private const ulong G2 = 967625415375836421;
    private const ulong G3 = 4543825880697944938;
    private static readonly FE gResidue = new(G0, G1, G2, G3);

    private const ulong QM0 = 9223372034707292161;
    private const ulong QM1 = 12240451741123816959;
    private const ulong QM2 = 1845609449319885826;
    private const ulong QM3 = 4176758429732224676;
    private static readonly FE qMinOne = new(QM0, QM1, QM2, QM3);

    public static Lazy<UInt256> _modulus = new(() =>
    {
        UInt256.TryParse("52435875175126190479447740508185965837690552500527637822603658699938581184513",
            out UInt256 output);
        return output;
    });

    public static Lazy<UInt256> _bLegendreExponentElement = new(() =>
    {
        UInt256 output =
            new(Convert.FromHexString("39f6d3a994cebea4199cec0404d0ec02a9ded2017fff2dff7fffffff80000000"),
                true);
        return output;
    });

    private static readonly Lazy<UInt256> _bSqrtExponentElement = new(() =>
    {
        UInt256 output = new(Convert.FromHexString("39f6d3a994cebea4199cec0404d0ec02a9ded2017fff2dff7fffffff"), true);
        return output;
    });

    private const int K = 32;
    private const ulong signBitSelector = (ulong)1 << 63;
    private const int approxLowBitsN = K - 1;
    private const int approxHighBitsN = K + 1;

    private const long updateFactorsConversionBias = 0x7fffffff7fffffff; // (2³¹ - 1)(2³² + 1)
    private const long updateFactorIdentityMatrixRow0 = 1;
    private const long updateFactorIdentityMatrixRow1 = 4294967296;
    private const int invIterationsN = 18;

    private const ulong inversionCorrectionFactorWord0 = 10120633560485349752;
    private const ulong inversionCorrectionFactorWord1 = 6708885176490223342;
    private const ulong inversionCorrectionFactorWord2 = 15589610060228208133;
    private const ulong inversionCorrectionFactorWord3 = 1857276366933877101;
}
