using System.Collections.Concurrent;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FpEElement;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;

// ReSharper disable InconsistentNaming

namespace Nethermind.Verkle.Proofs;

public class MultiProof(CRS cRs, PreComputedWeights preComp)
{
    private readonly int DomainSize = preComp.Domain.Length;

    private static void BatchNormalize(IReadOnlyList<VerkleProverQuery> queries, in Span<AffinePoint> normalizedPoints)
    {
        int numOfPoints = queries.Count;
        Span<FpE> zs = stackalloc FpE[numOfPoints];
        for (int i = 0; i < numOfPoints; i++) zs[i] = queries[i].NodeCommitPoint.Z;
        FpE[] inverses = FpE.MultiInverse(zs);
        for (int i = 0; i < numOfPoints; i++) normalizedPoints[i] = queries[i].NodeCommitPoint.ToAffine(inverses[i]);
    }
    public VerkleProofStruct MakeMultiProof(Transcript transcript, List<VerkleProverQuery> queries)
    {
        // batch normalize the NodeCommitPoints for the query
        Span<AffinePoint> normalizedCommitments = stackalloc AffinePoint[queries.Count];
        BatchNormalize(queries, normalizedCommitments);

        transcript.DomainSep("multiproof");
        for (int i = 0; i < queries.Count; i++)
        {
            transcript.AppendPoint(normalizedCommitments[i], "C");
            transcript.AppendScalar(queries[i].ChildIndex, "z");
            transcript.AppendScalar(queries[i].ChildHash, "y");
        }

        // calculate powers of r
        FrE r = transcript.ChallengeScalar("r");
        FrE[] powersOfR = new FrE[queries.Count];
        powersOfR[0] = FrE.One;
        FrE accumulator = FrE.One;
        for (int i = 1; i < queries.Count; i++)
        {
            FrE.MultiplyMod(in accumulator, in r, out accumulator);
            powersOfR[i] = accumulator;
        }

        // We aggregate all the polynomials in evaluation form per domain point
        // to avoid work downstream.
        HashSet<byte> evaluationPoints = [];
        foreach (VerkleProverQuery query in queries) evaluationPoints.Add(query.ChildIndex);

        LagrangeBasis[] scaledFArray = new LagrangeBasis[queries.Count];
        Parallel.ForEach(Partitioner.Create(0, queries.Count), partition =>
        {
            for (int i = partition.Item1; i < partition.Item2; i++)
                scaledFArray[i] = queries[i].ChildHashPoly * powersOfR[i];
        });

        int processorCount = Environment.ProcessorCount * 3;
        int rangeSize = (queries.Count + processorCount - 1) / processorCount;
        int numPartitions = (queries.Count + rangeSize - 1) / rangeSize;
        LagrangeBasis?[][] maps = new LagrangeBasis[numPartitions][];
        LagrangeBasis?[] aggregatedPolyMap = new LagrangeBasis[256];

        Parallel.ForEach(Partitioner.Create(0, queries.Count, rangeSize), tuple =>
        {
            LagrangeBasis?[] polyMap = new LagrangeBasis[256];
            for (int i = tuple.Item1; i < tuple.Item2; i++)
            {
                byte evaluationPoint = queries[i].ChildIndex;
                // evaluationPoints.Add(evaluationPoint);
                LagrangeBasis scaledF = scaledFArray[i];

                LagrangeBasis? poly = polyMap[evaluationPoint];
                if (poly is null)
                {
                    polyMap[evaluationPoint] = scaledF;
                    continue;
                }

                polyMap[evaluationPoint] = poly + scaledF;
            }

            maps[tuple.Item1 / rangeSize] = polyMap;
        });

        Parallel.ForEach(evaluationPoints, i =>
        {
            for (int j = 0; j < numPartitions; j++)
            {
                if (maps[j][i] is null) continue;
                if (aggregatedPolyMap[i] is null) aggregatedPolyMap[i] = maps[j][i];
                else aggregatedPolyMap[i] = aggregatedPolyMap[i]! + maps[j][i]!;
            }
        });

        Span<FrE> g = stackalloc FrE[256];
        Span<FrE> quotient = stackalloc FrE[256];
        foreach (byte i in evaluationPoints)
        {
            Quotient.ComputeQuotientInsideDomain(preComp, aggregatedPolyMap[i]!, i, quotient);
            for (int j = 0; j < g.Length; j++)
            {
                g[j] += quotient[j];
                quotient[j] = FrE.Zero;
            }
        }

        Banderwagon d = cRs.Commit(g);
        transcript.AppendPoint(d, "D");

        FrE t = transcript.ChallengeScalar("t");
        // We only will calculate inverses for domain points that are actually queried.
        Span<FrE> denominatorsInverse = stackalloc FrE[256];
        foreach (byte i in evaluationPoints) denominatorsInverse[i] = t - preComp.Domain[i];

        denominatorsInverse = FrE.MultiInverse(denominatorsInverse);

        Span<FrE> h = stackalloc FrE[256];
        foreach (byte i in evaluationPoints)
        {
            LagrangeBasis poly = aggregatedPolyMap[i]!;
            for (int j = 0; j < 256; j++) h[j] += poly.Evaluations[j] * denominatorsInverse[i];
        }

        Banderwagon e = cRs.Commit(h);
        transcript.AppendPoint(e, "E");

        Span<FrE> hMinusG = stackalloc FrE[256];
        for (int i = 0; i < 256; i++) hMinusG[i] = h[i] - g[i];

        Banderwagon ipaCommitment = e - d;

        Span<FrE> inputPointVector = stackalloc FrE[256];
        preComp.BarycentricFormulaConstants(t, inputPointVector);
        IpaProverQuery pQuery = new(hMinusG, ipaCommitment, t, inputPointVector);
        IpaProofStruct ipaProof = Ipa.MakeIpaProof(cRs, transcript, pQuery, out _);

        return new VerkleProofStruct(ipaProof, d);
    }
    public VerkleProofStructSerialized MakeMultiProofSerialized(VerkleProverQuerySerialized[] proverQueries)
    {
        byte[] input = new byte[proverQueries.Length * 8289];
        Span<byte> span = input;

        int offset = 0;

        foreach (VerkleProverQuerySerialized query in proverQueries)
        {
            query.Encode().CopyTo(span.Slice(offset, 8289));
            offset += 8289;
        }

        IntPtr ctx = RustVerkleLib.VerkleContextNew();

        byte[] output = new byte[1120];
        RustVerkleLib.ProveUncompressed(ctx, input, (UIntPtr)input.Length, output);

        byte[] d = output[0..64];

        IpaProofStructSerialized ipa_proof = IpaProofStructSerialized.CreateIpaProofSerialized(output);

        return new VerkleProofStructSerialized(ipa_proof, d);
    }

    public bool CheckMultiProof(Transcript transcript, VerkleVerifierQuery[] queries, VerkleProofStruct proof)
    {
        transcript.DomainSep("multiproof");
        foreach (VerkleVerifierQuery query in queries)
        {
            transcript.AppendPoint(query.NodeCommitPoint, "C");
            transcript.AppendScalar(query.ChildIndex, "z");
            transcript.AppendScalar(query.ChildHash, "y");
        }

        FrE r = transcript.ChallengeScalar("r");

        FrE[] powersOfR = new FrE[queries.Length];
        powersOfR[0] = FrE.One;
        for (int i = 1; i < queries.Length; i++) powersOfR[i] = powersOfR[i - 1] * r;

        Banderwagon d = proof.D;
        IpaProofStruct ipaProof = proof.IpaProof;
        transcript.AppendPoint(d, "D");
        FrE t = transcript.ChallengeScalar("t");

        // Calculate groupedEvals = r * y_i.
        FrE[] groupedEvals = new FrE[DomainSize];
        for (int i = 0; i < queries.Length; i++)
            groupedEvals[queries[i].ChildIndex] += powersOfR[i] * queries[i].ChildHash;

        // Compute helperScalarsDen = 1 / (t - z_i).
        FrE[] helperScalarDens = new FrE[DomainSize];
        foreach (byte childIndex in queries.Select(x => x.ChildIndex).Distinct())
            helperScalarDens[childIndex] = t - FrE.SetElement(childIndex);
        helperScalarDens = FrE.MultiInverse(helperScalarDens);

        // g2T = SUM [r^i * y_i] * [1 / (t - z_i)]
        FrE g2T = FrE.Zero;
        for (int i = 0; i < DomainSize; i++)
        {
            if (groupedEvals[i].IsZero) continue;
            g2T += groupedEvals[i] * helperScalarDens[i];
        }

        FrE[] helperScalars = new FrE[queries.Length];
        Banderwagon[] commitments = new Banderwagon[queries.Length];
        for (int i = 0; i < queries.Length; i++)
        {
            helperScalars[i] = helperScalarDens[queries[i].ChildIndex] * powersOfR[i];
            commitments[i] = queries[i].NodeCommitPoint;
        }

        Banderwagon g1Comm = Banderwagon.MultiScalarMul(commitments, helperScalars);

        transcript.AppendPoint(g1Comm, "E");

        FrE[] inputPointVector = preComp.BarycentricFormulaConstants(t);
        Banderwagon ipaCommitment = g1Comm - d;
        IpaVerifierQuery queryX = new(ipaCommitment, t, inputPointVector, g2T, ipaProof);

        return Ipa.CheckIpaProof(cRs, transcript, queryX);
    }

    public bool CheckMultiProofSerialized(VerkleVerifierQuerySerialized[] queries, VerkleProofStructSerialized proof)
    {
        byte[] input = new byte[1120 + 97 * queries.Length];
        Span<byte> span = input;

        proof.Encode().CopyTo(span.Slice(0, 1120));
        int offset = 1120;

        foreach (VerkleVerifierQuerySerialized query in queries)
        {
            query.Encode().CopyTo(span.Slice(offset, 97));
            offset += 97;
        }

        IntPtr ctx = RustVerkleLib.VerkleContextNew();

        return RustVerkleLib.VerifyUncompressed(ctx, input, (UIntPtr)input.Length);
    }
}
