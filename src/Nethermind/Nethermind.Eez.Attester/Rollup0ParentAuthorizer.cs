// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Prove.V2;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

internal sealed class Rollup0ParentAuthorizer
{
    private readonly SettlementRpcReader _l1;
    private readonly SettlementRpcReader _l2;
    private readonly ulong _l1ChainId;
    private readonly SettlementDeployment _deployment;
    internal EezSettlementContext Context { get; }
    internal Address Signer { get; }

    internal Rollup0ParentAuthorizer(SettlementRpcReader l1, SettlementRpcReader l2, ulong l1ChainId,
        Address registry, Address manager, EezSettlementContext context, Address signer)
    {
        if (l1ChainId == 0 || context.ChainId == 0 || context.RollupId == 0 || context.VerificationKey == default ||
            registry == Address.Zero || manager == Address.Zero || context.ProofSystem == Address.Zero || signer == Address.Zero)
            throw new ArgumentException("settlement authorization requires a complete trusted deployment");
        _l1 = l1;
        _l2 = l2;
        _l1ChainId = l1ChainId;
        _deployment = new SettlementDeployment(l1, registry, manager, context, signer);
        Context = context;
        Signer = signer;
    }

    internal async Task<L1AuthorizationSnapshot> Authorize(Begin predecessor, CancellationToken cancellation)
    {
        if (predecessor.RollupId != Context.RollupId) throw Rejected();
        if (SettlementRpcReader.Quantity(await _l1.Read("eth_chainId", [], cancellation).ConfigureAwait(false)) != _l1ChainId ||
            SettlementRpcReader.Quantity(await _l2.Read("eth_chainId", [], cancellation).ConfigureAwait(false)) != Context.ChainId)
            throw Rejected();

        byte[] parentRlp = SettlementRpcReader.Bytes(await _l2.Read("debug_getRawHeader",
            [predecessor.AnchorHash.ToByteArray().ToHexString(true)], cancellation).ConfigureAwait(false));
        BlockHeader parent;
        try { parent = Rlp.Decode<BlockHeader>(parentRlp) ?? throw Rejected(); }
        catch (Exception exception) when (exception is RlpException or ArgumentException or IndexOutOfRangeException)
        {
            throw Rejected();
        }
        if (parent.Number != predecessor.AnchorNumber || parent.Hash is null || parent.StateRoot is null ||
            !parent.Hash.Bytes.SequenceEqual(predecessor.AnchorHash.Span) ||
            !parent.StateRoot.Bytes.SequenceEqual(predecessor.AnchorStateRoot.Span) ||
            !Rlp.Encode(parent).Bytes.AsSpan().SequenceEqual(parentRlp))
            throw Rejected();

        L1AuthorizationSnapshot snapshot = await ReadSnapshot("latest", cancellation).ConfigureAwait(false);
        Hash256 root = await _deployment.ReadRoot(snapshot, cancellation).ConfigureAwait(false);
        if (!root.Bytes.SequenceEqual(predecessor.AnchorHash.Span)) throw Rejected();
        await _deployment.ValidateVerifier(snapshot, cancellation).ConfigureAwait(false);
        await Recheck(snapshot, cancellation).ConfigureAwait(false);
        return snapshot;
    }

    internal async Task Recheck(L1AuthorizationSnapshot snapshot, CancellationToken cancellation)
    {
        L1AuthorizationSnapshot canonical = await ReadSnapshot("0x" + snapshot.Number.ToString("x", CultureInfo.InvariantCulture), cancellation).ConfigureAwait(false);
        if (canonical != snapshot) throw new RpcException(new Status(StatusCode.Aborted, "settlement target no longer canonical"));
    }

    private async Task<L1AuthorizationSnapshot> ReadSnapshot(string number, CancellationToken cancellation)
    {
        JsonElement block = await _l1.Read("eth_getBlockByNumber", [number, false], cancellation, SettlementRpcReader.BlockResponseLimit).ConfigureAwait(false);
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("number", out JsonElement height) ||
            !block.TryGetProperty("hash", out JsonElement hash)) throw SettlementRpcReader.Unavailable();
        byte[] bytes = SettlementRpcReader.Bytes(hash);
        if (bytes.Length != 32 || !bytes.AsSpan().ContainsAnyExcept((byte)0)) throw SettlementRpcReader.Unavailable();
        return new L1AuthorizationSnapshot(SettlementRpcReader.Quantity(height), new Hash256(bytes));
    }

    private static RpcException Rejected() => new(new Status(StatusCode.FailedPrecondition, "settlement authorization rejected"));
}

internal sealed record L1AuthorizationSnapshot(ulong Number, Hash256 Hash);
