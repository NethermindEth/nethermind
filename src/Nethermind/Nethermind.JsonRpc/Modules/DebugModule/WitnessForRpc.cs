// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class WitnessForRpc(Witness witness)
{
    public IReadOnlyList<WitnessHeaderForRpc> Headers { get; } = ProjectHeaders(witness);
    public IReadOnlyDictionary<string, byte[]> Codes { get; } = BuildHashMap(witness.Codes);
    public IReadOnlyDictionary<string, byte[]> State { get; } = BuildHashMap(witness.State);

    private static IReadOnlyList<WitnessHeaderForRpc> ProjectHeaders(Witness witness)
    {
        using ArrayPoolList<Core.BlockHeader> decoded = witness.DecodeHeaders();
        WitnessHeaderForRpc[] headers = new WitnessHeaderForRpc[decoded.Count];
        for (int i = 0; i < decoded.Count; i++)
            headers[i] = new WitnessHeaderForRpc(decoded[i]);
        return headers;
    }

    private static Dictionary<string, byte[]> BuildHashMap(IOwnedReadOnlyList<byte[]> items)
    {
        Dictionary<string, byte[]> map = new(items.Count);
        foreach (byte[] item in items)
            map[ValueKeccak.Compute(item).ToString()] = item;
        return map;
    }
}
