// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Eip8141;

/// <summary>
/// A signature entry of an EIP-8141 frame transaction: <c>[scheme, signer, msg, signature]</c>.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public class FrameSignature(byte scheme, Address? signer, byte[] msg, byte[] signature)
{
    public const byte SchemeArbitrary = 0x0;
    public const byte SchemeSecp256k1 = 0x1;
    public const byte SchemeP256 = 0x2;

    public byte Scheme { get; } = scheme;

    /// <summary>Null resolves to the transaction sender. Must be null for <see cref="SchemeArbitrary"/>.</summary>
    public Address? Signer { get; } = signer;

    /// <summary>Empty means the canonical transaction signature hash; otherwise an explicit 32-byte digest.</summary>
    public byte[] Msg { get; } = msg;

    public byte[] Signature { get; } = signature;

    public bool SignsCanonicalHash => Msg.Length == 0;
}
