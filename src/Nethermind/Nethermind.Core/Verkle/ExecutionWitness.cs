// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using FastEnumUtility;
using Nethermind.Core.Crypto;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Core.Verkle;


public class ExecutionWitness
{
    public StemStateDiff[] StateDiff { get; }
    public WitnessVerkleProof? VerkleProof { get; }

    public Hash256 ParentStateRoot { get; }

    public ExecutionWitness()
    {
        StateDiff = [];
        VerkleProof = null;
        ParentStateRoot = Keccak.EmptyTreeHash;
    }

    public ExecutionWitness(StemStateDiff[] stateDiff, WitnessVerkleProof proof, Hash256 parentStateRoot)
    {
        StateDiff = stateDiff;
        VerkleProof = proof;
        ParentStateRoot = parentStateRoot;
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

public struct StemStateDiff : IEquatable<StemStateDiff>
{
    public Stem Stem { get; set; }
    public List<SuffixStateDiff> SuffixDiffs { get; set; }

    public bool Equals(StemStateDiff other)
    {
        return Stem.Equals(other.Stem) && SuffixDiffs.SequenceEqual(other.SuffixDiffs);
    }

    public override bool Equals(object? obj)
    {
        return obj is StemStateDiff other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Stem, SuffixDiffs);
    }
}

public struct SuffixStateDiff : IEquatable<SuffixStateDiff>
{
    public byte Suffix { get; set; }
    // add null if the values are not there - part of the spec
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? CurrentValue { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public byte[]? NewValue { get; set; }

    public bool Equals(SuffixStateDiff other)
    {
        if (Suffix != other.Suffix)
            return false;

        if (CurrentValue is null)
            return other.CurrentValue is null;

        return other.CurrentValue is not null &&
               Extensions.Bytes.AreEqual(CurrentValue, other.CurrentValue);
    }

    public override bool Equals(object? obj)
    {
        return obj is SuffixStateDiff other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Suffix, CurrentValue);
    }
}
