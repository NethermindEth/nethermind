// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Eez.Execution;

/// <summary>Computes a complete execution genesis commitment without opening a node database.</summary>
/// <remarks>Only allocation-based genesis documents are supported; constructors and postprocessors are not run.</remarks>
internal static class EezGenesisHash
{
    internal static Hash256 Compute(ChainSpec spec)
    {
        Block genesis = spec.Genesis ?? throw new InvalidDataException("Missing execution genesis.");
        StateTree state = new(NullTrieStore.Instance, NullLogManager.Instance);
        if (spec.Allocations is { } allocations)
        {
            foreach ((Address address, ChainSpecAllocation allocation) in allocations)
            {
                if (allocation.Constructor is not null) throw new InvalidDataException("Genesis constructors are not supported.");
                Hash256 storageRoot = ComputeStorageRoot(allocation.Storage);
                Hash256 codeHash = allocation.Code is { Length: > 0 } code ? Keccak.Compute(code) : Keccak.OfAnEmptyString;
                state.Set(address, new Account(allocation.Nonce, allocation.Balance, storageRoot, codeHash));
            }
        }
        state.UpdateRootHash();
        genesis.Header.StateRoot = state.RootHash;
        return genesis.Header.Hash = genesis.Header.CalculateHash();
    }

    private static Hash256 ComputeStorageRoot(Dictionary<UInt256, byte[]>? slots)
    {
        if (slots is not { Count: > 0 }) return Keccak.EmptyTreeHash;
        StorageTree storage = new(NullTrieStore.Instance, NullLogManager.Instance);
        foreach ((UInt256 key, byte[] value) in slots)
        {
            ReadOnlySpan<byte> word = value.AsSpan().WithoutLeadingZeros();
            if (word.Length > AbiWord.Size) throw new InvalidDataException("Genesis storage exceeds 256 bits.");
            storage.Set(key, word);
        }
        storage.UpdateRootHash();
        return storage.RootHash;
    }
}
