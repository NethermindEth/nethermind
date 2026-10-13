// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
    private readonly Address _registry;
    private readonly Address _manager;
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
        _registry = registry;
        _manager = manager;
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
        byte[] rollup = await Call(_registry, Calldata("rollups(uint64)", RollupWord(Context.RollupId)), snapshot, cancellation).ConfigureAwait(false);
        if (rollup.Length != 96 || !IsAddress(rollup.AsSpan(0, 32), _manager) ||
            !rollup.AsSpan(32, 32).SequenceEqual(predecessor.AnchorHash.Span)) throw Rejected();
        byte[] vkey = await Call(_manager, Calldata("verificationKey(address)", AddressWord(Context.ProofSystem)), snapshot, cancellation).ConfigureAwait(false);
        if (!vkey.AsSpan().SequenceEqual(Context.VerificationKey.Bytes)) throw Rejected();
        byte[] signer = await Call(Context.ProofSystem, Calldata("signer()", []), snapshot, cancellation).ConfigureAwait(false);
        if (signer.Length != 32 || !IsAddress(signer, Signer)) throw Rejected();
        byte[] threshold = await Call(_manager, Calldata("threshold()", []), snapshot, cancellation).ConfigureAwait(false);
        if (threshold.Length != 32 || threshold.AsSpan(0, 31).ContainsAnyExcept((byte)0) || threshold[31] > 1) throw Rejected();
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

    private async Task<byte[]> Call(Address target, byte[] data, L1AuthorizationSnapshot snapshot, CancellationToken cancellation) =>
        SettlementRpcReader.Bytes(await _l1.Read("eth_call",
            [new { to = target.ToString(), data = data.ToHexString(true) }, new { blockHash = snapshot.Hash.ToString(), requireCanonical = true }], cancellation).ConfigureAwait(false));

    private static bool IsAddress(ReadOnlySpan<byte> word, Address address) =>
        word.Length == 32 && !word[..12].ContainsAnyExcept((byte)0) && word[12..].SequenceEqual(address.Bytes);

    private static byte[] AddressWord(Address address)
    {
        byte[] word = new byte[32];
        address.Bytes.CopyTo(word.AsSpan(12));
        return word;
    }

    private static byte[] RollupWord(ulong rollupId)
    {
        byte[] word = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(word.AsSpan(24), rollupId);
        return word;
    }

    private static byte[] Calldata(string signature, byte[] arguments)
    {
        byte[] data = new byte[4 + arguments.Length];
        Keccak.Compute(signature).Bytes[..4].CopyTo(data);
        arguments.CopyTo(data.AsSpan(4));
        return data;
    }

    private static RpcException Rejected() => new(new Status(StatusCode.FailedPrecondition, "settlement authorization rejected"));
}

internal sealed record L1AuthorizationSnapshot(ulong Number, Hash256 Hash);
