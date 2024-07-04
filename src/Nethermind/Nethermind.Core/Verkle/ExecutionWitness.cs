// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using FastEnumUtility;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Core.Verkle;

public class ExecutionWitness
{
    public StemStateDiff[] StateDiff { get; }
    public WitnessVerkleProofSerialized? VerkleProof { get; }

    public ExecutionWitness()
    {
        StateDiff = Array.Empty<StemStateDiff>();
        VerkleProof = null;
    }

    public ExecutionWitness(StemStateDiff[] stateDiff, WitnessVerkleProofSerialized proof)
    {
        StateDiff = stateDiff;
        VerkleProof = proof;
    }
}

public class WitnessVerkleProofSerialized
{
    public Stem[]? OtherStems { get; set; }
    public byte[] DepthExtensionPresent { get; set; }
    public Banderwagon[] CommitmentsByPath { get; set; }
    public byte[] D { get; set; }

    public IpaProofStructSerialized IpaProof { get; set; }

    public WitnessVerkleProofSerialized(
        Stem[] otherStems,
        byte[] depthExtensionPresent,
        Banderwagon[] commitmentsByPath,
        byte[] d,
        IpaProofStructSerialized ipaProof
    )
    {
        OtherStems = otherStems;
        DepthExtensionPresent = depthExtensionPresent;
        CommitmentsByPath = commitmentsByPath;
        D = d;
        IpaProof = ipaProof;
    }

    public static implicit operator WitnessVerkleProofSerialized(VerkleProofSerialized proof)
    {
        Stem[] otherStems = proof.VerifyHint.DifferentStemNoProof.Select(x => new Stem(x)).ToArray();

        byte[] depthExtensionPresent = new byte[proof.VerifyHint.ExtensionPresent.Length];
        for (int i = 0; i < depthExtensionPresent.Length; i++)
        {
            depthExtensionPresent[i] = (byte)(proof.VerifyHint.Depths[i] << 3);
            depthExtensionPresent[i] =
                (byte)(depthExtensionPresent[i] | (proof.VerifyHint.ExtensionPresent[i].ToByte()));
        }

        return new WitnessVerkleProofSerialized(otherStems,
            depthExtensionPresent,
            proof.CommsSorted,
            proof.Proof.D,
            proof.Proof.IpaProofSerialized
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
    // add null if the values are not there - part of the spec
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? CurrentValue { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? NewValue { get; set; }
}
