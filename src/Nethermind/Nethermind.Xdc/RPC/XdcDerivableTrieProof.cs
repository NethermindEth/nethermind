// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Xdc.RPC;

public sealed class XdcDerivableTrieProof
{
    public string[] Keys { get; init; } = [];
    public string[] Values { get; init; } = [];

    public static XdcDerivableTrieProof FromProofNodes(byte[][] proofNodes)
    {
        string[] keys = new string[proofNodes.Length];
        string[] values = new string[proofNodes.Length];
        for (int i = 0; i < proofNodes.Length; i++)
        {
            byte[] rlp = proofNodes[i];
            keys[i] = Keccak.Compute(rlp).ToString();
            values[i] = Bytes.ToHexString(rlp);
        }

        return new XdcDerivableTrieProof { Keys = keys, Values = values };
    }
}
