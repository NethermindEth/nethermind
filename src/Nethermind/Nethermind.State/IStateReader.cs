// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public interface IStateReader
    {
        bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account);
        /// <summary>Reads a storage slot at the selected block, returning zero for a missing account or slot.</summary>
        void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value);

        /// <summary>Reads the storage slots <paramref name="indexes"/> of one account at the selected block into <paramref name="values"/>.</summary>
        void GetStorage(BlockHeader? baseBlock, Address address, ReadOnlySpan<UInt256> indexes, Span<UInt256> values)
        {
            for (int i = 0; i < indexes.Length; i++)
            {
                GetStorage(baseBlock, address, in indexes[i], out values[i]);
            }
        }
        byte[]? GetCode(Hash256 codeHash);
        byte[]? GetCode(in ValueHash256 codeHash);
        /// <summary>
        /// Run a tree visitor against the state at <paramref name="baseBlock"/>. When
        /// <paramref name="diagnostics"/> is non-null, the resolver is wrapped with metering and
        /// per-call lookup, cache-miss, and depth counters are accumulated into it (used by
        /// <c>proof_getProofWithMeta</c>).
        /// </summary>
        void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx>;

        bool HasStateForBlock(BlockHeader? baseBlock);
    }
}
