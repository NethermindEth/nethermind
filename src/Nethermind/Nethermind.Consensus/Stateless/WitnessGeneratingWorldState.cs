// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Decorates the base world state to assemble an execution <see cref="Witness"/>. The storage portion
/// (state-trie + storage-trie nodes and touched keys) is produced by the scope itself — opened with
/// <c>trackWitness</c> — and read back via <see cref="IWorldState.Witness"/>. This decorator only adds the
/// pieces the scope does not track: contract bytecode read during execution and the ancestor block headers.
/// </summary>
public class WitnessGeneratingWorldState(
    IWorldState state,
    WitnessGeneratingHeaderFinder headerFinder)
    : WorldStateDecorator(state)
{
    private readonly Dictionary<ValueHash256, byte[]> _bytecodes =
        new(GenericEqualityComparer.GetOptimized<ValueHash256>());

    /// <summary>Clears the per-call bytecode accumulator so this instance can be reused across pooled rents.</summary>
    public void Reset() => _bytecodes.Clear();

    public Witness GetWitness(BlockHeader parentHeader)
    {
        ScopeWitness scopeWitness = State.Witness
            ?? throw new InvalidOperationException("Witness tracking was not enabled for this scope.");

        // New pool-rented buffers added here must also be disposed in the catch below.
        ArrayPoolList<byte[]>? codes = null;
        ArrayPoolList<byte[]>? state = null;
        ArrayPoolList<byte[]>? keys = null;
        try
        {
            codes = new ArrayPoolList<byte[]>(_bytecodes.Count);
            foreach (byte[] code in _bytecodes.Values)
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

    public override byte[]? GetCode(Address address)
    {
        byte[]? code = base.GetCode(address);
        RecordBytecode(code);
        return code;
    }

    public override byte[]? GetCode(in ValueHash256 codeHash)
    {
        byte[]? code = base.GetCode(in codeHash);
        // The hash is already known here, so skip re-Keccaking the (potentially large) bytecode —
        // DELEGATECALL loops to the same contract would otherwise pay it on every read.
        RecordBytecode(in codeHash, code);
        return code;
    }

    public override void RecordBytecodeAccess(Address address) => GetCode(address);

    private void RecordBytecode(byte[]? code)
    {
        // The Address-keyed paths don't carry the code hash, so compute it here.
        if (code?.Length > 0)
            RecordBytecode(ValueKeccak.Compute(code), code);
    }

    private void RecordBytecode(in ValueHash256 codeHash, byte[]? code)
    {
        // Unnecessary to record empty code
        if (code?.Length > 0)
            _bytecodes.TryAdd(codeHash, code);
    }
}
