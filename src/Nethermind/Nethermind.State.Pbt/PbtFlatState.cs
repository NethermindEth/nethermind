// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Derives canonical tree leaves from whole flat values without retaining a second flat index.</summary>
internal static class PbtFlatState
{
    internal static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> AccountLeaves(ValueHash256 addressHash, Account account, CodeInfo? code)
    {
        PbtAccount stem = PbtAccount.From(account, code);
        if (stem.BasicData != default) yield return new(PbtStateKey.Account(addressHash, PbtKeyDerivation.BasicDataLeafKey), stem.BasicData);
        if (stem.IsDelegation)
        {
            yield return new(PbtStateKey.Account(addressHash, PbtKeyDerivation.DelegationLeafKey), stem.CodeLeaf);
            yield break;
        }
        yield return new(PbtStateKey.Account(addressHash, PbtKeyDerivation.CodeHashLeafKey), stem.CodeLeaf);
        if (code is null) yield break;
        foreach (KeyValuePair<PbtPath, ValueHash256> leaf in CodeLeaves(stem.CodeLeaf, code)) yield return leaf;
    }

    /// <summary>The code-chunk leaves of <paramref name="code"/>, omitting all-zero chunks as the tree does.</summary>
    internal static IEnumerable<KeyValuePair<PbtPath, ValueHash256>> CodeLeaves(ValueHash256 codeHash, CodeInfo code)
    {
        int codeLength = code.Code.Length;
        int chunkCount = (codeLength + 30) / 31;
        int chunksLength = chunkCount * PbtKeyDerivation.CodeChunkSize;
        using ArrayPoolList<byte> chunks = new(chunksLength, chunksLength);
        PbtKeyDerivation.ChunkifyCode(code.CodeSpan[..codeLength], chunks.AsSpan());
        for (int chunkId = 0; chunkId < chunkCount; chunkId++)
        {
            ValueHash256 value = new(chunks.AsSpan().Slice(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize));
            if (value != default) yield return new(PbtStateKey.Code(codeHash, chunkId), value);
        }
    }
}
