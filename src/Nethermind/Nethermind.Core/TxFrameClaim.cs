// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// A declarative claim of an EIP-8141 frame transaction: <c>[verifier, key, msg, gas_limit, witness_len]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
/// <remarks>The claim asserts that <see cref="Verifier"/> attests the resolved key authorizes the resolved message.
/// The witness that satisfies it is segregated from the transaction body, carried in the pooled wrapper and in the
/// block's dependency proof, so it is held on <see cref="Transaction.ClaimWitnesses"/> rather than here.</remarks>
public sealed class TxFrameClaim(Address verifier, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> msg, ulong gasLimit, ulong witnessLength)
{
    /// <summary>A protocol verifier, one of the <c>*Verifier</c> addresses of <see cref="Eip8141Constants"/>, or a
    /// custom verifier: an active precompile or a non-delegated contract run in the pure context.</summary>
    public Address Verifier { get; } = verifier;

    /// <summary>Empty for the sender, or a non-zero 20-byte address or 32-byte value; see <see cref="ResolveKey"/>.</summary>
    public ReadOnlyMemory<byte> Key { get; } = key;

    /// <summary>Empty means the canonical transaction signature hash; otherwise an explicit non-zero 32-byte digest.</summary>
    public ReadOnlyMemory<byte> Msg { get; } = msg;

    /// <summary>The execution gas a custom verifier is given, charged in full; zero for a protocol verifier.</summary>
    public ulong GasLimit { get; } = gasLimit;

    /// <summary>The exact witness length a custom verifier takes; zero for a protocol verifier.</summary>
    public ulong WitnessLength { get; } = witnessLength;

    /// <summary>Whether <see cref="Msg"/> is empty, so the claim is over the canonical signature hash.</summary>
    public bool SignsCanonicalHash => Msg.IsEmpty;

    /// <summary>Whether <see cref="Verifier"/> is one of the claim-only protocol verifiers.</summary>
    public bool IsProtocolVerifier => Eip8141Constants.TryGetProtocolVerifier(Verifier, out _, out _);

    /// <summary>EIP-8141 <c>resolve_claim_gas</c>: the verification gas the claim is charged.</summary>
    public ulong ResolvedGas => Eip8141Constants.TryGetProtocolVerifier(Verifier, out ulong gas, out _) ? gas : GasLimit;

    /// <summary>EIP-8141 <c>resolve_witness_len</c>: the exact length the claim's witness must have.</summary>
    public ulong ResolvedWitnessLength => Eip8141Constants.TryGetProtocolVerifier(Verifier, out _, out ulong length) ? length : WitnessLength;

    /// <summary>EIP-8141 <c>resolve_key</c>: the sender or a 20-byte key left-padded to 32 bytes, or a 32-byte key as is.</summary>
    /// <remarks>Assumes <see cref="Key"/> is empty, 20 or 32 bytes long, as <c>FrameTxValidation.IsWellFormed</c> checks.</remarks>
    public ValueHash256 ResolveKey(Address sender)
    {
        ValueHash256 resolved = default;
        ReadOnlySpan<byte> key = Key.IsEmpty ? sender.Bytes : Key.Span;
        key.CopyTo(resolved.BytesAsSpan[(ValueHash256.MemorySize - key.Length)..]);
        return resolved;
    }

    /// <summary>The digest the claim is over: <paramref name="sigHash"/> for an empty <see cref="Msg"/>.</summary>
    /// <remarks>Assumes <see cref="Msg"/> is empty or 32 bytes long.</remarks>
    public ValueHash256 ResolveMsg(in ValueHash256 sigHash) => Msg.IsEmpty ? sigHash : new ValueHash256(Msg.Span);
}
