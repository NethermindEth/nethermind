// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Runtime.InteropServices.ComTypes;
using Nethermind.Core.Verkle;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Serializers;

namespace Nethermind.Synchronization.VerkleSync;

public static class VerkleProofExtensions
{
    public static byte[] EncodeRlp(this VerkleProof proof)
    {
        if (proof.CommsSorted is null || proof.CommsSorted.Length == 0) return Array.Empty<byte>();
        VerkleProofSerializer ser = VerkleProofSerializer.Instance;
        var encodedData = new RlpStream(Rlp.LengthOfSequence(ser.GetLength(proof, RlpBehaviors.None)));
        ser.Encode(encodedData, proof);
        return encodedData.Data.ToArray()!;
    }
}
