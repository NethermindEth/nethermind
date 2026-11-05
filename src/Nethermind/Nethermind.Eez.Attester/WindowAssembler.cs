// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Execution.Stateless;

namespace Nethermind.Eez.Attester;

/// <summary>
/// Admits one <c>Prove</c> stream chunk by chunk: the header first, then every block of the declared span in order,
/// each within the request quotas. A chunk that breaks a rule fails the request as soon as it arrives.
/// </summary>
internal sealed class WindowAssembler
{
    private readonly WindowLimits _limits;
    private readonly PreparedCache? _cache;
    private readonly List<PreparedOrdinaryBlock?> _prepared = [];
    private readonly List<PreparedCache.BorrowedRange> _borrows = [];
    private long _cachedBytes;
    private readonly ProveHeader _header;
    private readonly int _declared;
    private readonly List<EezStatelessBlock> _blocks;
    private readonly List<(Hash256 Hash, Hash256 ParentHash)> _claims;
    private long _bytes;
    private long _witnessItems;

    private WindowAssembler(WindowLimits limits, ProveHeader header, int declared, long bytes, PreparedCache? cache)
    {
        _limits = limits;
        _cache = cache;
        _header = header;
        _declared = declared;
        _bytes = bytes;
        _blocks = new List<EezStatelessBlock>(declared);
        _claims = new List<(Hash256, Hash256)>(declared);
    }

    public ulong RollupId => _header.RollupId;

    /// <exception cref="RpcException">The first chunk is not an admissible header.</exception>
    public static WindowAssembler Start(WindowLimits limits, ProveChunk first, ByteString? nativeIdentity = null, PreparedCache? cache = null)
    {
        long bytes = Charge(0, CanonicalSize(first), limits.MaxBytes);
        ProveHeader header = first.KindCase switch
        {
            ProveChunk.KindOneofCase.Header => first.Header,
            ProveChunk.KindOneofCase.Block => throw Invalid($"first chunk must be the window header, got block {first.Block.Number}"),
            _ => throw Invalid("chunk at index 0 carries no kind"),
        };

        if (header.PostBatch is null)
        {
            throw Invalid("header carries no post_batch");
        }

        if (nativeIdentity is null)
        {
            if (header.ExpectedValidationIdentity.Length != 0 || header.ParentProvenance is not null ||
                header.PostBatch.Blobs.Count != 0 || header.PostBatch.PostingContext is not null)
                throw Invalid("native fields are not supported by the Rollup0 profile");
        }
        else
        {
            if (header.ExpectedValidationIdentity != nativeIdentity)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "native validation identity differs"));
            if (header.ParentProvenance is null || header.ParentProvenance.ParentHeaderRlp.Length == 0 ||
                header.PostBatch.Blobs.Count == 0 || header.PostBatch.PostingContext is null || header.PostBatch.PublicInputsHash.Length != 0)
                throw Invalid("native provenance, blobs and posting context are required without legacy claims");
            NativeProver.Predecessor(header);
        }

        if (header.PostBatch.L1BlockHash.Length != 0)
        {
            throw Invalid($"header post_batch carries a {header.PostBatch.L1BlockHash.Length}-byte l1_block_hash; it must be empty");
        }

        if (header.FromBlock == 0 || header.FromBlock > header.ToBlock)
        {
            throw Invalid($"invalid window bounds {header.FromBlock}..={header.ToBlock}");
        }

        ulong span = header.ToBlock - header.FromBlock + 1;
        if (span > (ulong)limits.MaxBlocks)
        {
            throw Quota($"window {header.FromBlock}..={header.ToBlock} spans {span} blocks, limit is {limits.MaxBlocks}");
        }

        return new WindowAssembler(limits, header.Clone(), (int)span, bytes, nativeIdentity is null ? null : cache);
    }

    /// <exception cref="RpcException">The chunk is not the next admissible block.</exception>
    public void Push(ProveChunk chunk)
    {
        _bytes = Charge(_bytes, CanonicalSize(chunk), _limits.MaxBytes - _cachedBytes);
        if (chunk.KindCase == ProveChunk.KindOneofCase.CachedRange)
        {
            PushCached(chunk.CachedRange);
            return;
        }
        BlockWitness block = chunk.KindCase switch
        {
            ProveChunk.KindOneofCase.Block => chunk.Block,
            ProveChunk.KindOneofCase.Header => throw Invalid("duplicate header chunk"),
            _ => throw Invalid($"chunk at index {1 + _blocks.Count} carries no kind"),
        };

        int index = _claims.Count;
        if (index >= _declared)
        {
            throw Invalid($"window already carries its declared {_declared} blocks");
        }

        ulong expected = _header.FromBlock + (ulong)index;
        if (block.Number != expected)
        {
            throw Invalid($"expected block {expected} at block index {index}, got {block.Number}");
        }

        if (block.Hash.Length != Hash256.Size)
        {
            throw Invalid($"block {block.Number} has a {block.Hash.Length}-byte block hash");
        }

        if (block.ParentHash.Length != Hash256.Size)
        {
            throw Invalid($"block {block.Number} has a {block.ParentHash.Length}-byte parent hash");
        }

        ExecutionWitness witness = block.Witness ?? throw Invalid($"block {block.Number} carries no execution witness");
        RejectL1Evidence(witness);
        long attempted = _witnessItems + witness.State.Count + witness.Codes.Count + witness.Keys.Count + witness.Headers.Count;
        if (attempted > _limits.MaxWitnessItems)
        {
            throw Quota($"window witness reached {attempted} items, limit is {_limits.MaxWitnessItems}");
        }

        _witnessItems = attempted;
        Hash256 hash = new(block.Hash.Span);
        Hash256 parentHash = new(block.ParentHash.Span);
        if (index > 0 && parentHash != _claims[index - 1].Hash)
        {
            throw Invalid($"hash-chain break at block {block.Number}: expected parent {_claims[index - 1].Hash} from block {expected - 1}, got {parentHash}");
        }

        _claims.Add((hash, parentHash));
        _blocks.Add(new EezStatelessBlock(block.Rlp.ToByteArray(), ToL2Witness(witness)));
        _prepared.Add(null);
    }

    /// <exception cref="RpcException">The stream ended before the declared span.</exception>
    public AdmittedWindow Finish()
    {
        if (_claims.Count != _declared)
        {
            throw Invalid($"window {_header.FromBlock}..={_header.ToBlock} expects {_declared} blocks, stream ended after {_blocks.Count}");
        }

        return new AdmittedWindow(_header.FromBlock, _header.ToBlock, _header.PostBatch.AbiCalldata.ToByteArray(), _blocks.ToArray(), _claims.ToArray())
        {
            NativeHeader = _header.ParentProvenance is null ? null : _header,
            Prepared = _borrows.Count == 0 ? [] : _prepared.ToArray(),
            CacheBorrows = _borrows.ToArray(),
        };
    }

    public void Abandon()
    {
        foreach (PreparedCache.BorrowedRange range in _borrows) range.Dispose();
        foreach (EezStatelessBlock block in _blocks)
        {
            block.Witness.Dispose();
        }
    }

    private void PushCached(CachedOrdinaryRange selection)
    {
        if (_cache is null) throw new RpcException(new Status(StatusCode.Unimplemented, "cached ordinary ranges are not supported"));
        ulong count = PreparedCache.RangeLength(selection);
        if (selection.FirstNumber != _header.FromBlock + (ulong)_claims.Count || count >= (ulong)(_declared - _claims.Count))
            throw Invalid("cached range must cover the next ordinary blocks and leave a fresh terminal witness");
        PreparedCache.BorrowedRange borrowed = _cache.Borrow(selection);
        try
        {
            long bytes = _cachedBytes;
            foreach (PreparedOrdinaryBlock fact in borrowed.Facts)
                bytes = Charge(bytes, fact.RetainedBytes, _limits.MaxBytes - _bytes);
            if (_claims.Count != 0 && borrowed.Facts[0].ParentHash != _claims[^1].Hash)
                throw Invalid("cached range breaks the selected hash chain");
            _cachedBytes = bytes;
            foreach (PreparedOrdinaryBlock fact in borrowed.Facts)
            {
                _claims.Add((fact.Hash, fact.ParentHash));
                _prepared.Add(fact);
            }
            _borrows.Add(borrowed);
        }
        catch
        {
            borrowed.Dispose();
            throw;
        }
    }

    public static RpcException Invalid(string reason) => new(new Status(StatusCode.InvalidArgument, $"window: {reason}"));

    private static RpcException Quota(string reason) => new(new Status(StatusCode.ResourceExhausted, $"window quota: {reason}"));

    private static long Charge(long total, long size, long limit)
    {
        long attempted = total + size;
        return attempted <= limit ? attempted : throw Quota($"window payload reached {attempted} bytes, limit is {limit}");
    }

    internal static Witness ToWitness(ExecutionWitness witness)
    {
        RejectL1Evidence(witness);
        return ToL2Witness(witness);
    }

    internal static void RejectL1Evidence(ExecutionWitness witness)
    {
        if (witness.L1HeadersRlp.Count != 0) throw Invalid("L1 header evidence is not supported by this profile");
    }

    /// <summary>Copies L2 execution inputs; native callers must separately validate and retain field 5.</summary>
    internal static Witness ToL2Witness(ExecutionWitness witness) => new()
    {
        State = ToList(witness.State),
        Codes = ToList(witness.Codes),
        Keys = ToList(witness.Keys),
        Headers = ToList(witness.Headers),
    };

    private static ArrayPoolList<byte[]> ToList(IReadOnlyList<ByteString> items)
    {
        ArrayPoolList<byte[]> list = new(items.Count);
        foreach (ByteString item in items)
        {
            list.Add(item.ToByteArray());
        }

        return list;
    }

    internal static long CanonicalSize(ProveChunk chunk) => chunk.CalculateSize();
}
