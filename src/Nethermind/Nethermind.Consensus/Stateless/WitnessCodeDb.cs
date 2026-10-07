// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>Serves a witness' codes by hash, as the very memory they were handed over in.</summary>
/// <remarks>
/// A code handed over in executable code memory then runs without being copied again. The witness codes are never
/// written to the underlying <see cref="MemDb"/>; code the block itself deploys is, and is read back from there.
/// </remarks>
internal sealed class WitnessCodeDb : MemDb, IWorldStateScopeProvider.ICodeDb
{
    private readonly Dictionary<ValueHash256, ReadOnlyMemory<byte>> _codes;
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _deployed;

    public WitnessCodeDb(IReadOnlyList<ReadOnlyMemory<byte>> codes)
    {
        _codes = new Dictionary<ValueHash256, ReadOnlyMemory<byte>>(codes.Count);
        foreach (ReadOnlyMemory<byte> code in codes)
        {
            _codes[ValueKeccak.Compute(code.Span)] = code;
        }

        _deployed = new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(this);
    }

    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) =>
        _codes.TryGetValue(codeHash, out ReadOnlyMemory<byte> code) ? code : _deployed.GetCode(in codeHash);

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => _deployed.BeginCodeWrite();
}
