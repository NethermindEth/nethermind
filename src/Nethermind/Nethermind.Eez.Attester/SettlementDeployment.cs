// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Attester;

internal sealed class SettlementDeployment
{
    private readonly SettlementRpcReader _l1;
    private readonly Address _registry;
    private readonly Address _manager;
    internal EezSettlementContext Context { get; }
    internal Address Signer { get; }

    internal SettlementDeployment(SettlementRpcReader l1, Address registry, Address manager,
        EezSettlementContext context, Address signer)
    {
        if (context.ChainId == 0 || context.RollupId == 0 || context.VerificationKey == default || registry == Address.Zero ||
            manager == Address.Zero || context.ProofSystem == Address.Zero || signer == Address.Zero)
            throw new ArgumentException("settlement authorization requires a complete trusted deployment");
        _l1 = l1;
        _registry = registry;
        _manager = manager;
        Context = context;
        Signer = signer;
    }

    internal async Task<Hash256> ReadRoot(L1AuthorizationSnapshot snapshot, CancellationToken cancellation)
    {
        byte[] rollup = await Call(_registry, Calldata("rollups(uint64)", RollupWord(Context.RollupId)), snapshot, cancellation).ConfigureAwait(false);
        if (rollup.Length != 96 || !IsAddress(rollup.AsSpan(0, 32), _manager)) throw Rejected();
        return new Hash256(rollup.AsSpan(32, 32));
    }

    internal async Task ValidateVerifier(L1AuthorizationSnapshot snapshot, CancellationToken cancellation)
    {
        byte[] vkey = await Call(_manager, Calldata("verificationKey(address)", AddressWord(Context.ProofSystem)), snapshot, cancellation).ConfigureAwait(false);
        if (!vkey.AsSpan().SequenceEqual(Context.VerificationKey.Bytes)) throw Rejected();
        byte[] signer = await Call(Context.ProofSystem, Calldata("signer()", []), snapshot, cancellation).ConfigureAwait(false);
        if (signer.Length != 32 || !IsAddress(signer, Signer)) throw Rejected();
        byte[] threshold = await Call(_manager, Calldata("threshold()", []), snapshot, cancellation).ConfigureAwait(false);
        if (threshold.Length != 32 || threshold.AsSpan(0, 31).ContainsAnyExcept((byte)0) || threshold[31] > 1) throw Rejected();
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
