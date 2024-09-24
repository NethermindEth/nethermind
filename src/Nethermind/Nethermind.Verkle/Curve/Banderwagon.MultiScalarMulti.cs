// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Threading.Tasks.Dataflow;
using Nethermind.Verkle.Fields.FpEElement;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Curve;

public readonly partial struct Banderwagon
{
    private static void BatchNormalize(in ReadOnlySpan<Banderwagon> points, in Span<AffinePoint> normalizedPoints)
    {
        int numOfPoints = points.Length;
        var zs = new FpE[numOfPoints];
        for (int i = 0; i < numOfPoints; i++) zs[i] = points[i].Z;

        FpE[] inverses = FpE.MultiInverse(zs);
        for (int i = 0; i < numOfPoints; i++) normalizedPoints[i] = points[i].ToAffine(inverses[i]);
    }

    public static Banderwagon MultiScalarMul(in ReadOnlySpan<Banderwagon> points, Span<FrE> scalars, int threadCount = 0)
    {
        var normalizedPoint = new AffinePoint[points.Length];
        BatchNormalize(points, normalizedPoint);
        return MultiScalarMulFast(normalizedPoint, scalars.ToArray(), threadCount);
    }

    private static FrE[] BatchConvertFromMontgomery(FrE[] scalarsMont, ExecutionDataflowBlockOptions options)
    {
        FrE[] scalars = new FrE[scalarsMont.Length];
        ActionBlock<int> setBlock = new ActionBlock<int>((i) =>
        {
            FrE.FromMontgomery(in scalarsMont[i], out scalars[i]);
        }, options);
        for (int i = 0; i < scalarsMont.Length; i++)
        {
            setBlock.Post(i);
        }
        setBlock.Complete();
        setBlock.Completion.Wait();

        return scalars;
    }


    private static Banderwagon MultiScalarMulFast(IReadOnlyList<AffinePoint> points, FrE[] scalars, int threadCount)
    {
        int numOfPoints = points.Count;
        int windowsSize = numOfPoints < 32 ? 3 : (int)(Math.Log2(numOfPoints) * 69 / 100) + 2;
        // const int windowsSize = 3;

        int i = 0;
        List<int> windowsStart = [];

        while (i < 253)
        {
            windowsStart.Add(i);
            i += windowsSize;
        }

        ulong bucketSize = ((ulong)1 << windowsSize) - 1;

        ExecutionDataflowBlockOptions options = new()
        {
            MaxDegreeOfParallelism = threadCount == 0 ? Environment.ProcessorCount : threadCount
        };
        FrE[] scalarsReg = BatchConvertFromMontgomery(scalars, options);

        Banderwagon[] windowSums = new Banderwagon[windowsStart.Count];

        ActionBlock<int> windowAction = new((w) =>
        {
            int winStart = windowsStart[w];

            Banderwagon res = Identity;
            Banderwagon[] buckets = new Banderwagon[bucketSize];

            for (int j = 0; j < buckets.Length; j++) buckets[j] = Identity;

            for (int j = 0; j < points.Count; j++)
                if (scalarsReg[j].IsRegularOne)
                {
                    if (winStart == 0) res = Add(res, points[j]);
                }
                else
                {
                    FrE scalar = scalarsReg[j];
                    scalar >>= winStart;

                    ulong sc = scalar.u0;
                    sc %= (ulong)1 << windowsSize;

                    if (sc != 0) buckets[sc - 1] = Add(buckets[sc - 1], points[j]);
                }

            Banderwagon runningSum = Identity;
            for (int j = buckets.Length - 1; j >= 0; j--)
            {
                runningSum += buckets[j];
                res += runningSum;
            }

            windowSums[w] = res;
        }, options);

        for (int j = 0; j < windowsStart.Count; j++)
        {
            windowAction.Post(j);
        }
        windowAction.Complete();
        windowAction.Completion.Wait();

        Banderwagon lowest = windowSums[0];

        Banderwagon result = Identity;
        for (int j = windowSums.Length - 1; j > 0; j--)
        {
            result += windowSums[j];
            for (int k = 0; k < windowsSize; k++) result = Double(result);
        }

        result += lowest;
        return result;
    }
}
