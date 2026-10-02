// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// A signature entry of an EIP-8141 frame transaction: <c>[scheme, signer, msg, signature]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public class TxFrameSignature(byte scheme, Address? signer, ReadOnlyMemory<byte> msg, ReadOnlyMemory<byte> signature)
{
    /// <summary>An entry the protocol does not verify: opaque bytes whose meaning is left to the frame code
    /// that reads them.</summary>
    /// <remarks>Only the structural rules apply — it must name no <see cref="Signer"/>, and <see cref="Msg"/> is
    /// still held to the empty-or-non-zero-32-byte rule though nothing reads it; <see cref="Signature"/> alone is
    /// unconstrained. A frame relying on such an entry must verify the witness itself.</remarks>
    public const byte SchemeArbitrary = 0x0;

    /// <summary>An ECDSA signature over secp256k1, verified against the recovered address.</summary>
    /// <remarks><see cref="Signature"/> is <see cref="Secp256k1SignatureLength"/> bytes laid out as
    /// <c>v || r || s</c>, with <c>v</c> a single 0 or 1 byte and <c>s</c> required to be low.</remarks>
    public const byte SchemeSecp256k1 = 0x1;

    /// <summary>An ECDSA signature over secp256r1 (P-256), verified through the EIP-7951 precompile.</summary>
    /// <remarks><see cref="Signature"/> is <see cref="P256SignatureLength"/> bytes laid out as
    /// <c>r || s || qx || qy</c>, with <c>s</c> required to be low. The public key must hash to
    /// <see cref="Signer"/> under the usual <c>keccak(qx || qy)[12:]</c> derivation. An entry of this scheme is
    /// rejected on a chain where the precompile is not yet active.</remarks>
    public const byte SchemeP256 = 0x2;

    /// <summary>Exact length in bytes a <see cref="SchemeSecp256k1"/> <see cref="Signature"/> must have.</summary>
    public const int Secp256k1SignatureLength = 65;

    /// <summary>Exact length in bytes a <see cref="SchemeP256"/> <see cref="Signature"/> must have; it carries the
    /// public key as well as <c>r</c> and <c>s</c>.</summary>
    public const int P256SignatureLength = 128;

    /// <summary>Which of the <c>Scheme*</c> constants governs how <see cref="Signature"/> is read and verified.</summary>
    public byte Scheme { get; } = scheme;

    /// <summary>Null resolves to the transaction sender. Must be null for <see cref="SchemeArbitrary"/>.</summary>
    public Address? Signer { get; } = signer;

    /// <summary>Empty means the canonical transaction signature hash; otherwise an explicit 32-byte digest.</summary>
    public ReadOnlyMemory<byte> Msg { get; } = msg;

    /// <summary>The raw signature bytes, whose layout and required length are fixed by <see cref="Scheme"/>.</summary>
    public ReadOnlyMemory<byte> Signature { get; } = signature;

    /// <summary>Whether this entry leaves <see cref="Msg"/> empty, so a protocol-verified scheme signs the
    /// transaction's canonical signature hash rather than a digest of its own.</summary>
    /// <remarks>Every such entry of one transaction shares a digest, so it is computed once; entries carrying an
    /// explicit <see cref="Msg"/> never need it at all. The canonical hash elides these entries' own
    /// <see cref="Signature"/> bytes, which is what lets them sign a transaction that contains them. EIP-8141
    /// § Signature Hash keys that elision on the empty <c>msg</c> alone, so a <see cref="SchemeArbitrary"/> entry
    /// — which signs nothing the protocol checks — is elided too, and its bytes stay rewritable without
    /// disturbing any canonical-hash signature.</remarks>
    public bool SignsCanonicalHash => Msg.IsEmpty;
}
