using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Proofs;

public static class Ipa
{
    public static FrE InnerProduct(in ReadOnlySpan<FrE> a, in ReadOnlySpan<FrE> b)
    {
        FrE res = FrE.Zero;
        for (int i = 0; i < a.Length; i++) res += a[i] * b[i];

        return res;
    }

    public static IpaProofStruct MakeIpaProof(CRS crs, Transcript transcript, IpaProverQuery query, out FrE y)
    {
        transcript.DomainSep("ipa");

        int m = 128;
        Span<FrE> a = query.Polynomial;
        Span<FrE> b = query.PointEvaluations;
        y = InnerProduct(a, b);

        const int numRounds = 8;
        Banderwagon[] l = new Banderwagon[numRounds];
        Banderwagon[] r = new Banderwagon[numRounds];

        transcript.AppendPoint(query.Commitment, "C"u8.ToArray());
        transcript.AppendScalar(query.Point, "input point"u8.ToArray());
        transcript.AppendScalar(y, "output point"u8.ToArray());
        FrE w = transcript.ChallengeScalar("w"u8.ToArray());

        Banderwagon q = crs.BasisQ * w;

        Span<Banderwagon> currentBasis = crs.BasisG;

        for (int round = 0; round < numRounds; round++)
        {
            Span<FrE> aL = a[..m];
            Span<FrE> aR = a[m..];
            Span<FrE> bL = b[..m];
            Span<FrE> bR = b[m..];
            Span<Banderwagon> gL = currentBasis[..m];
            Span<Banderwagon> gR = currentBasis[m..];

            FrE zL = InnerProduct(aR, bL);
            FrE zR = InnerProduct(aL, bR);

            Banderwagon cL = Banderwagon.MultiScalarMul(gL, aR) + (q * zL);
            Banderwagon cR = Banderwagon.MultiScalarMul(gR, aL) + (q * zR);

            l[round] = cL;
            r[round] = cR;

            transcript.AppendPoint(cL, "L"u8.ToArray());
            transcript.AppendPoint(cR, "R"u8.ToArray());
            FrE x = transcript.ChallengeScalar("x"u8.ToArray());

            FrE.Inverse(x, out FrE xInv);

            a = new FrE[m];
            for (int i = 0; i < m; i++) a[i] = aL[i] + (x * aR[i]);

            b = new FrE[m];
            for (int i = 0; i < m; i++) b[i] = bL[i] + (xInv * bR[i]);


            currentBasis = new Banderwagon[m];
            for (int i = 0; i < m; i++) currentBasis[i] = gL[i] + (gR[i] * xInv);

            m /= 2;
        }

        return new IpaProofStruct(l, a[0], r);
    }

    public static bool CheckIpaProof(CRS crs, Transcript transcript,
        IpaVerifierQuery query)
    {
        transcript.DomainSep("ipa"u8.ToArray());

        int numRounds = query.IpaProof.L.Length;

        Banderwagon c = query.Commitment;
        FrE z = query.Point;
        Span<FrE> b = query.PointEvaluations;
        IpaProofStruct ipaProof = query.IpaProof;
        Span<Banderwagon> commitL = ipaProof.L;
        Span<Banderwagon> commitR = ipaProof.R;
        FrE y = query.OutputPoint;

        transcript.AppendPoint(c, "C"u8.ToArray());
        transcript.AppendScalar(z, "input point"u8.ToArray());
        transcript.AppendScalar(y, "output point"u8.ToArray());
        FrE w = transcript.ChallengeScalar("w"u8.ToArray());

        Banderwagon q = crs.BasisQ * w;

        FrE[] xs = new FrE[numRounds];
        for (int i = 0; i < numRounds; i++)
        {
            Banderwagon cL = commitL[i];
            Banderwagon cR = commitR[i];
            transcript.AppendPoint(cL, "L"u8.ToArray());
            transcript.AppendPoint(cR, "R"u8.ToArray());
            xs[i] = transcript.ChallengeScalar("x"u8.ToArray());
        }

        FrE[] xInvList = FrE.MultiInverse(xs);

        Banderwagon cLScaled = Banderwagon.MultiScalarMul(commitL, xs);
        Banderwagon cRScaled = Banderwagon.MultiScalarMul(commitR, xInvList);
        Banderwagon currentCommitment = c + (q * y) + cLScaled + cRScaled;

        Span<Banderwagon> currentBasis = crs.BasisG;

        // We apply a known optimization for the verifier unrolling the loop to
        // generate the final g0 and b0.
        FrE[] foldingScalars = new FrE[currentBasis.Length];
        for (int i = 0; i < foldingScalars.Length; i++)
        {
            FrE scalar = FrE.One;

            // We iterate on the bits of the challenge index from the MSB to LSB
            // and accumulate in scalar for the corresponding challenge.
            for (int challengeIdx = 0; challengeIdx < numRounds; challengeIdx++)
                if ((i & (1 << (numRounds - 1 - challengeIdx))) > 0)
                    scalar *= xInvList[challengeIdx];
            foldingScalars[i] = scalar;
        }

        FrE b0 = InnerProduct(b, foldingScalars);
        Banderwagon g0 = Banderwagon.MultiScalarMul(currentBasis, foldingScalars);
        Banderwagon gotCommitment = (g0 * ipaProof.A) + (q * (ipaProof.A * b0));

        return currentCommitment == gotCommitment;
    }
}
