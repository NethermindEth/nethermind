// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Assembles a full execution <see cref="Witness"/> from the storage <see cref="ScopeWitness"/> produced by a
/// witness-tracking scope (state-trie nodes, touched keys, contract code) plus the ancestor block headers.
/// </summary>
internal static class WitnessAssembler
{
    public static Witness Build(ScopeWitness scopeWitness, WitnessGeneratingHeaderFinder headerFinder, BlockHeader parentHeader)
    {
        // New pool-rented buffers added here must also be disposed in the catch below.
        ArrayPoolList<byte[]>? codes = null;
        ArrayPoolList<byte[]>? state = null;
        ArrayPoolList<byte[]>? keys = null;
        try
        {
            codes = new ArrayPoolList<byte[]>(scopeWitness.Codes.Count);
            foreach (byte[] code in scopeWitness.Codes)
                codes.Add(code);

            state = new ArrayPoolList<byte[]>(scopeWitness.StateNodes.Count);
            foreach (byte[] node in scopeWitness.StateNodes)
                state.Add(node);

            keys = new ArrayPoolList<byte[]>(scopeWitness.Keys.Count);
            foreach (byte[] key in scopeWitness.Keys)
                keys.Add(key);

            return new Witness
            {
                Codes = codes,
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
            codes?.Dispose();
            state?.Dispose();
            keys?.Dispose();
            throw;
        }
    }
}
