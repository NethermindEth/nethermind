// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Serialization.Rlp;
using V2 = Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class NativeProver(RollupXStreamingFinalizer finalizer, ByteString identity, AttesterOptions? settings = null)
{
    internal ByteString Identity { get; } = identity;
    internal PreparedCache Cache { get; } = new(identity, settings?.RequestTimeout ?? TimeSpan.FromMinutes(5));

    internal Task<PreparedOrdinaryBlock> Prepare(EezStatelessBlock block, CancellationToken cancellation) =>
        finalizer.Prepare(block, cancellation);

    internal CapabilitiesResponse Capabilities(AttesterOptions options) => new()
    {
        ProfileId = 1,
        PreparationMaxBlocks = (ulong)Math.Min(64, options.Limits.MaxBlocks),
        CacheMaxEntries = (ulong)Cache.MaxEntries,
        CacheMaxBytes = (ulong)Cache.MaxBytes,
        MaximumLeases = PreparedCache.MaximumLeases,
        LeaseLifetimeMillis = (ulong)Cache.LeaseLifetime.TotalMilliseconds,
        ValidationIdentity = Identity,
        MaxWindowBlocks = (ulong)options.Limits.MaxBlocks,
        MaxRequestBytes = (ulong)options.Limits.MaxBytes,
        MaxRequestWitnessItems = (ulong)options.Limits.MaxWitnessItems,
        MaxMessageBytes = (ulong)options.Limits.MaxMessageBytes,
        StreamIdleTimeoutMillis = (ulong)options.StreamIdleTimeout.TotalMilliseconds,
        RequestTimeoutMillis = (ulong)options.RequestTimeout.TotalMilliseconds,
    };

    internal Task<ProveResponse> Prove(AdmittedWindow window, CancellationToken cancellation)
    {
        ProveHeader header = window.NativeHeader ?? throw WindowAssembler.Invalid("native header missing");
        V2.SettlementInput settlement = new() { AbiCalldata = header.PostBatch.AbiCalldata };
        settlement.Blobs.Add(header.PostBatch.Blobs);
        L1PostingContext posting = header.PostBatch.PostingContext;
        settlement.PostingContext = new V2.L1PostingContext
        {
            ChainId = posting.ChainId,
            TargetTimestamp = posting.TargetTimestamp,
            ParentNumber = posting.ParentNumber,
            ParentHash = posting.ParentHash,
        };
        return finalizer.Prove(window, settlement, Predecessor(header), header.ParentProvenance, cancellation);
    }

    internal static V2.Begin Predecessor(ProveHeader header)
    {
        try
        {
            ByteString encoded = header.ParentProvenance?.ParentHeaderRlp ?? ByteString.Empty;
            BlockHeader parent = Rlp.Decode<BlockHeader>(encoded.ToByteArray()) ?? throw WindowAssembler.Invalid("native predecessor missing");
            if (parent.Number == ulong.MaxValue || parent.Number + 1 != header.FromBlock || parent.Hash is null || parent.StateRoot is null ||
                !Rlp.Encode(parent).Bytes.AsSpan().SequenceEqual(encoded.Span))
                throw WindowAssembler.Invalid("native predecessor differs from window");
            return new V2.Begin
            {
                RollupId = header.RollupId,
                AnchorNumber = parent.Number,
                AnchorHash = ByteString.CopyFrom(parent.Hash.Bytes),
                AnchorStateRoot = ByteString.CopyFrom(parent.StateRoot.Bytes),
            };
        }
        catch (Exception exception) when (exception is RlpException or ArgumentException or IndexOutOfRangeException)
        {
            throw WindowAssembler.Invalid("invalid native predecessor header");
        }
    }

    internal static ByteString ValidationIdentity(AttesterOptions options, StreamingOptions settings, EezSettlementContext context)
    {
        byte[] identity = Rlp.Encode(
            Rlp.Encode(Encoding.UTF8.GetBytes("nethermind.eez.native-inline-v1/1")),
            Rlp.Encode(typeof(NativeProver).Module.ModuleVersionId.ToByteArray()),
            Rlp.Encode(typeof(EezSettlementContext).Module.ModuleVersionId.ToByteArray()),
            Rlp.Encode(Keccak.Compute(StreamingOptions.ReadFile(options.ChainConfigPath, 8 * 1024 * 1024)).Bytes),
            Rlp.Encode(Keccak.Compute(StreamingOptions.ReadFile(settings.L1ChainConfigPath!, 8 * 1024 * 1024)).Bytes),
            Rlp.Encode(context.RollupId), Rlp.Encode(context.ChainId), Rlp.Encode(context.BlockTimeSeconds), Rlp.Encode(context.GasLimit),
            Rlp.Encode(context.ProofSystem.Bytes), Rlp.Encode(context.VerificationKey.Bytes),
            Rlp.Encode(settings.L1ChainId), Rlp.Encode(settings.Registry!.Bytes), Rlp.Encode(settings.Manager!.Bytes),
            Rlp.Encode(settings.L2GenesisHash!.Bytes)).Bytes;
        return ByteString.CopyFrom(Keccak.Compute(identity).Bytes);
    }
}
