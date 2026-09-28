// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Attester.Rpc;
using Nethermind.Eez.Execution.Stateless;

namespace Nethermind.Eez.Attester;

/// <summary>
/// Admits one <c>Prove</c> stream chunk by chunk: the header first, then every block of the declared span in order,
/// each within the request quotas. A chunk that breaks a rule fails the request as soon as it arrives.
/// </summary>
internal sealed class WindowAssembler
{
    private readonly WindowLimits _limits;
    private readonly ProveHeader _header;
    private readonly int _declared;
    private readonly List<EezStatelessBlock> _blocks;
    private readonly List<(Hash256 Hash, Hash256 ParentHash)> _claims;
    private long _bytes;
    private long _witnessItems;

    private WindowAssembler(WindowLimits limits, ProveHeader header, int declared, long bytes)
    {
        _limits = limits;
        _header = header;
        _declared = declared;
        _bytes = bytes;
        _blocks = new List<EezStatelessBlock>(declared);
        _claims = new List<(Hash256, Hash256)>(declared);
    }

    public ulong RollupId => _header.RollupId;

    /// <exception cref="RpcException">The first chunk is not an admissible header.</exception>
    public static WindowAssembler Start(WindowLimits limits, ProveChunk first)
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

        return new WindowAssembler(limits, header, (int)span, bytes);
    }

    /// <exception cref="RpcException">The chunk is not the next admissible block.</exception>
    public void Push(ProveChunk chunk)
    {
        _bytes = Charge(_bytes, CanonicalSize(chunk), _limits.MaxBytes);
        BlockWitness block = chunk.KindCase switch
        {
            ProveChunk.KindOneofCase.Block => chunk.Block,
            ProveChunk.KindOneofCase.Header => throw Invalid("duplicate header chunk"),
            _ => throw Invalid($"chunk at index {1 + _blocks.Count} carries no kind"),
        };

        int index = _blocks.Count;
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
        _blocks.Add(new EezStatelessBlock(block.Rlp.ToByteArray(), ToWitness(witness)));
    }

    /// <exception cref="RpcException">The stream ended before the declared span.</exception>
    public AdmittedWindow Finish()
    {
        if (_blocks.Count != _declared)
        {
            throw Invalid($"window {_header.FromBlock}..={_header.ToBlock} expects {_declared} blocks, stream ended after {_blocks.Count}");
        }

        return new AdmittedWindow(_header.FromBlock, _header.ToBlock, _header.PostBatch.AbiCalldata.ToByteArray(), _blocks.ToArray(), _claims.ToArray());
    }

    public void Abandon()
    {
        foreach (EezStatelessBlock block in _blocks)
        {
            block.Witness.Dispose();
        }
    }

    public static RpcException Invalid(string reason) => new(new Status(StatusCode.InvalidArgument, $"window: {reason}"));

    private static RpcException Quota(string reason) => new(new Status(StatusCode.ResourceExhausted, $"window quota: {reason}"));

    private static long Charge(long total, long size, long limit)
    {
        long attempted = total + size;
        return attempted <= limit ? attempted : throw Quota($"window payload reached {attempted} bytes, limit is {limit}");
    }

    private static Witness ToWitness(ExecutionWitness witness) => new()
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

    /// <summary>The canonical protobuf size of the chunk's known fields, which is what the quota counts.</summary>
    internal static long CanonicalSize(ProveChunk chunk) => chunk.KindCase switch
    {
        ProveChunk.KindOneofCase.Header => Field(1, Size(chunk.Header)),
        ProveChunk.KindOneofCase.Block => Field(2, Size(chunk.Block)),
        _ => 0,
    };

    private static long Size(ProveHeader header) =>
        UInt64(1, header.RollupId) + UInt64(2, header.FromBlock) + UInt64(3, header.ToBlock) + (header.PostBatch is null ? 0 : Field(4, Size(header.PostBatch)));

    private static long Size(PostBatch batch) => Bytes(1, batch.AbiCalldata) + Bytes(2, batch.PublicInputsHash) + Bytes(3, batch.L1BlockHash);

    private static long Size(BlockWitness block) =>
        UInt64(1, block.Number) + Bytes(2, block.Hash) + Bytes(3, block.ParentHash) + Bytes(4, block.Rlp) + (block.Witness is null ? 0 : Field(5, Size(block.Witness)));

    private static long Size(ExecutionWitness witness) => Repeated(1, witness.State) + Repeated(2, witness.Codes) + Repeated(3, witness.Keys) + Repeated(4, witness.Headers);

    private static long UInt64(int field, ulong value) => value == 0 ? 0 : CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeUInt64Size(value);

    private static long Bytes(int field, ByteString value) => value.Length == 0 ? 0 : Field(field, value.Length);

    private static long Repeated(int field, IReadOnlyList<ByteString> values)
    {
        long size = 0;
        foreach (ByteString value in values)
        {
            size += Field(field, value.Length);
        }

        return size;
    }

    private static long Field(int field, long length) => CodedOutputStream.ComputeTagSize(field) + CodedOutputStream.ComputeLengthSize(checked((int)length)) + length;
}
