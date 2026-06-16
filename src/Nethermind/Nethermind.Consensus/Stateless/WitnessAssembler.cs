// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Assembles a full execution <see cref="Witness"/> from its independently produced parts: the storage
/// witness (state-trie node RLPs), the touched keys and contract code, and the ancestor block headers.
/// </summary>
internal static class WitnessAssembler
{
    public static Witness Build(
        IReadOnlyList<byte[]> stateNodes,
        IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> touchedKeys,
        IReadOnlyCollection<byte[]> codes,
        WitnessGeneratingHeaderFinder headerFinder,
        BlockHeader parentHeader)
    {
        // New pool-rented buffers added here must also be disposed in the catch below.
        ArrayPoolList<byte[]>? codeList = null;
        ArrayPoolList<byte[]>? state = null;
        ArrayPoolList<byte[]>? keys = null;
        try
        {
            codeList = new ArrayPoolList<byte[]>(codes.Count);
            foreach (byte[] code in codes)
                codeList.Add(code);

            state = new ArrayPoolList<byte[]>(stateNodes.Count);
            foreach (byte[] node in stateNodes)
                state.Add(node);

            int totalKeysCount = touchedKeys.Count;
            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in touchedKeys)
                totalKeysCount += kvp.Value.Count;

            keys = new ArrayPoolList<byte[]>(totalKeysCount);
            // Keys ordered like: <addr1><addr2><slot1-of-addr2><slot2-of-addr2><addr3><slot1-of-addr3>
            foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> kvp in touchedKeys)
            {
                keys.Add(kvp.Key.Value.Bytes.ToArray());
                foreach (UInt256 slot in kvp.Value)
                    keys.Add(slot.ToBigEndian());
            }

            return new Witness
            {
                Codes = codeList,
                State = state,
                Keys = keys,
                Headers = headerFinder.GetWitnessHeaders(parentHeader.Hash!)
            };
        }
        catch
        {
            // Any failure mid-build returns the rented buffers before propagating, else they leak:
            // an OOM while filling a list, or GetWitnessHeaders throwing because a walked ancestor
            // header vanished (reorg/prune between the call and the witness build).
            codeList?.Dispose();
            state?.Dispose();
            keys?.Dispose();
            throw;
        }
    }
}
