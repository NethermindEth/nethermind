// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FastEnumUtility;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Core.Verkle;


public class ExecutionWitness
{
    public List<StemStateDiff> StateDiff { get; set; } = new(0);
    public WitnessVerkleProof? VerkleProof { get; set; } = null;

    public ExecutionWitness() { }

    public ExecutionWitness(List<StemStateDiff> stateDiff, WitnessVerkleProof proof)
    {
        StateDiff = stateDiff;
        VerkleProof = proof;
    }
}

public class WitnessVerkleProof
{
    public Stem[]? OtherStems { get; set; }
    public byte[] DepthExtensionPresent { get; set; }
    public Banderwagon[] CommitmentsByPath { get; set; }
    public Banderwagon D { get; set; }
    public IpaProofStruct IpaProof { get; set; }

    public WitnessVerkleProof(
        Stem[] otherStems,
        byte[] depthExtensionPresent,
        Banderwagon[] commitmentsByPath,
        Banderwagon d,
        IpaProofStruct ipaProof)
    {
        OtherStems = otherStems;
        DepthExtensionPresent = depthExtensionPresent;
        CommitmentsByPath = commitmentsByPath;
        D = d;
        IpaProof = ipaProof;
    }

    public static implicit operator WitnessVerkleProof(VerkleProof proof)
    {
        Stem[] otherStems = proof.VerifyHint.DifferentStemNoProof.Select(x => new Stem(x)).ToArray();

        byte[] depthExtensionPresent = new byte[proof.VerifyHint.ExtensionPresent.Length];
        for (int i = 0; i < depthExtensionPresent.Length; i++)
        {
            depthExtensionPresent[i] = (byte)(proof.VerifyHint.Depths[i] << 3);
            depthExtensionPresent[i] =
                (byte)(depthExtensionPresent[i] | (proof.VerifyHint.ExtensionPresent[i].ToByte()));
        }

        return new WitnessVerkleProof(otherStems,
            depthExtensionPresent,
            proof.CommsSorted,
            proof.Proof.D,
            proof.Proof.IpaProof
        );
    }
}


public struct StateDiff
{
    public List<StemStateDiff> SuffixDiffs { get; set; }
}

public struct StemStateDiff
{
    public Stem Stem { get; set; }
    public List<SuffixStateDiff> SuffixDiffs { get; set; }
}

public struct SuffixStateDiff
{
    public byte Suffix { get; set; }
    public byte[]? CurrentValue { get; set; }
    public byte[]? NewValue { get; set; }
}
