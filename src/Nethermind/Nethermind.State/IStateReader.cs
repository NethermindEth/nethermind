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
        ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index);
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
