// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Extensions;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;
using Nethermind.Verkle.Utils;

namespace Nethermind.Verkle.Tree.Proofs;

public class ListComparer : Comparer<List<byte>>
{
    public override int Compare(List<byte>? x, List<byte>? y)
    {
        if (x is null)
        {
            return y is null ? 0 : 1;
        }

        if (y is null)
        {
            return -1;
        }
        return Bytes.Comparer.Compare(x.ToArray(), y.ToArray());
    }
}

public class ListWithByteComparer : Comparer<(List<byte>, byte)>
{
    public override int Compare((List<byte>, byte) x, (List<byte>, byte) y)
    {
        ListComparer comp = new ListComparer();
        int val = comp.Compare(x.Item1, y.Item1);
        return val == 0 ? x.Item2.CompareTo(y.Item2) : val;
    }
}


public static class Verifier
{
    public static (bool, UpdateHint?) VerifyVerkleProof(VerkleProof proof, List<byte[]> keys, List<byte[]?> values, Banderwagon root)
    {
        List<Banderwagon> commSortedByPath = new() { root };
        commSortedByPath.AddRange(proof.CommsSorted);

        SortedSet<byte[]> stems = new(keys.Select(x => x[..31]), Bytes.Comparer);
        SortedDictionary<byte[], (ExtPresent, byte)> depthsAndExtByStem = new(Bytes.Comparer);
        SortedSet<byte[]> stemsWithExtension = new(Bytes.Comparer);
        SortedSet<byte[]> otherStemsUsed = new(Bytes.Comparer);
        SortedSet<List<byte>> allPaths = new(new ListComparer());
        SortedSet<(List<byte>, byte)> allPathsAndZs = new(new ListWithByteComparer());
        SortedDictionary<(List<byte>, byte), FrE> leafValuesByPathAndZ = new(new ListWithByteComparer());
        SortedDictionary<List<byte>, byte[]> otherStemsByPrefix = new(new ListComparer());


        foreach (((byte[] stem, byte depth), ExtPresent extPresent) in stems.Zip(proof.VerifyHint.Depths).Zip(proof.VerifyHint.ExtensionPresent))
        {
            depthsAndExtByStem.Add(stem, (extPresent, depth));
            switch (extPresent)
            {
                case ExtPresent.Present:
                    stemsWithExtension.Add(stem);
                    break;
                case ExtPresent.None:
                case ExtPresent.DifferentStem:
                    break;
                default:
                    throw new ArgumentException($"impossible value for the enum {extPresent}");
            }
        }

        foreach ((byte[] key, byte[]? value) in keys.Zip(values))
        {
            byte[] stem = key[..31];
            (ExtPresent extPres, byte depth) = depthsAndExtByStem[stem];

            for (int i = 0; i < depth; i++)
            {
                allPaths.Add(new List<byte>(stem[..i]));
                allPathsAndZs.Add((new List<byte>(stem[..i]), stem[i]));
            }

            switch (extPres)
            {
                case ExtPresent.DifferentStem:

                    allPaths.Add(new List<byte>(stem[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 1));

                    leafValuesByPathAndZ.Add((new List<byte>(stem[..depth]), 0), FrE.One);

                    // since the stem was different - value should not have been set
                    if (value != null) return (false, null);

                    Debug.Assert(depth != stem.Length);

                    byte[] otherStem;

                    byte[][] found = stemsWithExtension.Where(x => x[..depth].SequenceEqual(stem[..depth])).ToArray();

                    switch (found.Length)
                    {
                        case 0:
                            found = proof.VerifyHint.DifferentStemNoProof.Where(x => x[..depth].SequenceEqual(stem[..depth])).ToArray();
                            byte[] encounteredStem = found[^1];
                            otherStem = encounteredStem;
                            otherStemsUsed.Add(encounteredStem);

                            // Add extension node to proof in particular, we only want to open at (1, stem)
                            leafValuesByPathAndZ.Add((new List<byte>(stem[..depth]), 1), FrE.FromBytesReduced(encounteredStem.Reverse().ToArray()));
                            break;
                        case 1:
                            otherStem = found[0];
                            break;
                        default:
                            throw new NotSupportedException($"found more than one instance of stem_with_extension at depth {depth}, see: {string.Join(" | ", found.Select(x => string.Join(", ", x)))}");
                    }

                    otherStemsByPrefix.Add(stem[..depth].ToList(), otherStem);
                    break;
                case ExtPresent.Present:
                    allPaths.Add(new List<byte>(stem[..depth]));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 0));
                    allPathsAndZs.Add((new List<byte>(stem[..depth]), 1));

                    leafValuesByPathAndZ.Add((new List<byte>(stem[..depth]), 0), FrE.One);
                    if (extPres == ExtPresent.Present)
                    {
                        byte suffix = key[31];
                        byte openingIndex = suffix < 128 ? (byte)2 : (byte)3;

                        allPathsAndZs.Add((new List<byte>(stem[..depth]), openingIndex));
                        leafValuesByPathAndZ.Add((new List<byte>(stem[..depth]), 1), FrE.FromBytesReduced(stem.Reverse().ToArray()));

                        List<byte> suffixTreePath = new(stem[..depth]);
                        suffixTreePath.Add(openingIndex);

                        allPaths.Add(suffixTreePath);
                        byte valLowerIndex = (byte)(2 * (suffix % 128));
                        byte valUpperIndex = (byte)(valLowerIndex + 1);

                        allPathsAndZs.Add((suffixTreePath, valLowerIndex));
                        allPathsAndZs.Add((suffixTreePath, valUpperIndex));

                        (FrE valLow, FrE valHigh) = VerkleUtils.BreakValueInLowHigh(value);

                        leafValuesByPathAndZ.Add((suffixTreePath, valLowerIndex), valLow);
                        leafValuesByPathAndZ.Add((suffixTreePath, valUpperIndex), valHigh);
                    }
                    break;
                case ExtPresent.None:
                    // If the extension was not present, then the value should be None
                    if (value != null) return (false, null);

                    if (depth == 1)
                    {
                        leafValuesByPathAndZ.Add((new List<byte>(), stem[depth - 1]), FrE.Zero);
                    }
                    else
                    {
                        leafValuesByPathAndZ.Add(
                            (stem[..depth].ToList(), stem[depth - 1]), FrE.Zero
                            );
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        Debug.Assert(proof.VerifyHint.DifferentStemNoProof.SequenceEqual(otherStemsUsed));
        Debug.Assert(commSortedByPath.Count == allPaths.Count);

        SortedDictionary<List<byte>, Banderwagon> commByPath = new(new ListComparer());
        foreach ((List<byte> path, Banderwagon comm) in allPaths.Zip(commSortedByPath))
        {
            commByPath[path] = comm;
        }

        SortedDictionary<(List<byte>, byte), Banderwagon> commByPathAndZ = new(new ListWithByteComparer());
        foreach ((List<byte> path, byte z) in allPathsAndZs)
        {
            commByPathAndZ[(path, z)] = commByPath[path];
        }

        SortedDictionary<(List<byte>, byte), FrE> ysByPathAndZ = new(new ListWithByteComparer());
        foreach ((List<byte> path, byte z) in allPathsAndZs)
        {
            List<byte> childPath = new(path.ToArray())
            {
                z
            };

            if (!leafValuesByPathAndZ.TryGetValue((path, z), out FrE y))
            {
                y = FrE.FromBytesReduced(commByPath[childPath].MapToField());
            }
            ysByPathAndZ.Add((path, z), y);
        }

        SortedDictionary<(List<byte>, byte), Banderwagon>.ValueCollection cs = commByPathAndZ.Values;

        IEnumerable<FrE> zs = allPathsAndZs.Select(elem => FrE.SetElement(elem.Item2));
        SortedDictionary<(List<byte>, byte), FrE>.ValueCollection ys = ysByPathAndZ.Values;

        List<VerkleVerifierQuery> queries = new(cs.Count);

        foreach (((FrE y, FrE z), Banderwagon comm) in ys.Zip(zs).Zip(cs))
        {
            VerkleVerifierQuery query = new(comm, z, y);
            queries.Add(query);
        }

        UpdateHint updateHint = new()
        {
            DepthAndExtByStem = depthsAndExtByStem,
            CommByPath = commByPath,
            DifferentStemNoProof = otherStemsByPrefix
        };

        Transcript proverTranscript = new("vt");
        MultiProof proofVerifier = new(CRS.Instance, PreComputeWeights.Init());

        return (proofVerifier.CheckMultiProof(proverTranscript, queries.ToArray(), proof.Proof), updateHint);
    }
}
