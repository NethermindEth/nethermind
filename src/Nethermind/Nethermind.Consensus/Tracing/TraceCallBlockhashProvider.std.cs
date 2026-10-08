// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Consensus.Tracing;

#pragma warning disable NETH003 // Build variant: excluded from the zkEVM build, which does no tracing
internal sealed class TraceCallBlockhashProvider(
    IBlockhashProvider inner,
    IBlockTree blockTree,
    GethStyleTracer.TraceCallRequestState state) : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
    {
        if (state.BlockhashLookup is not { } lookup || spec.IsBlockHashInStateAvailable)
            return inner.GetBlockhash(currentBlock, number, spec);

        if (number >= currentBlock.Number || currentBlock.Number - number > BlockhashProvider.MaxDepth)
            return null;

        return lookup.GetHash(blockTree, number);
    }

    public bool TryGetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec, out ReadOnlySpan<byte> hash)
    {
        if (state.BlockhashLookup is null || spec.IsBlockHashInStateAvailable)
            return inner.TryGetBlockhash(currentBlock, number, spec, out hash);

        Hash256? blockHash = GetBlockhash(currentBlock, number, spec);
        hash = blockHash is null ? default : blockHash.Bytes;
        return blockHash is not null;
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => inner.Prefetch(currentBlock, token);

    internal sealed class Lookup(BlockHeader reference, ulong currentNumber)
    {
        private readonly ulong _referenceNumber = reference.Number;
        private ulong _number = reference.Number == 0 ? 0 : reference.Number - 1;
        private Hash256? _hash = reference.ParentHash;
        private Dictionary<ulong, Hash256?>? _hashes;
        private bool _exhausted;

        internal CancellationToken Token { get; set; }

        internal Hash256? GetHash(IBlockTree blockTree, ulong number)
        {
            Token.ThrowIfCancellationRequested();
            if (number >= _referenceNumber) return null;
            if (_hashes is not null && _hashes.TryGetValue(number, out Hash256? cached)) return cached;
            if (_exhausted) return null;

            while (_number > number)
            {
                CacheCurrent();
                Token.ThrowIfCancellationRequested();
                BlockHeader? ancestor = _hash is null ? null : blockTree.FindHeader(_hash, BlockTreeLookupOptions.None);
                if (ancestor is null || ancestor.Number != _number)
                {
                    _exhausted = true;
                    return null;
                }
                _hash = ancestor.ParentHash;
                _number--;
            }
            CacheCurrent();
            return _hash;
        }

        private void CacheCurrent()
        {
            // Backward number overrides may skip millions of ancestors, but only 256 can be queried by this call.
            if (_number < currentNumber && currentNumber - _number <= BlockhashProvider.MaxDepth)
                (_hashes ??= [])[_number] = _hash;
        }
    }
}
