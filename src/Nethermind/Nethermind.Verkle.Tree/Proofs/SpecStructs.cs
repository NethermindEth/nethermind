// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Verkle.Tree.Proofs;

public struct VProof
{
    public VerkleProofStruct _multiPoint;
    public List<byte> _extStatus;
    public List<Banderwagon> _cS;
    public List<byte[]> _poaStems;
    public List<byte[]> _keys;
    public List<byte[]> _values;
}

public struct ExecutionWitness
{
    public StateDiff StateDiff;
    public VProof Proof;
}
public struct SuffixStateDiff
{
    public byte Suffix;

    // byte32
    public byte[]? CurrentValue;

    // byte32
    public byte[]? NewValue;
}

public struct StemStateDiff
{
    // byte31
    public byte[] Stem;

    // max length = 256
    public List<SuffixStateDiff> SuffixDiffs;
}

public struct StateDiff
{
    // max length = 2**16
    public List<StemStateDiff> Diff;
}


// public class VerkleProofGenVerify
// {
//     public static
// }
