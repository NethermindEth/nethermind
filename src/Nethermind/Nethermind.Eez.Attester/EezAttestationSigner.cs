// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Eez.Attester;

/// <summary>
/// Signs public inputs hashes for an <c>ECDSAProofSystem</c>: the raw digest, without a message prefix, as
/// <c>abi.encodePacked(r, s, v)</c> with a low <c>s</c> and <c>v</c> of 27 or 28.
/// </summary>
public sealed class EezAttestationSigner(PrivateKey key)
{
    public const int SignatureLength = 65;

    private static readonly Ecdsa Ecdsa = new();

    public Address Address => key.Address;

    public byte[] Sign(in ValueHash256 publicInputsHash)
    {
        Signature signature = Ecdsa.Sign(key, publicInputsHash);
        if (signature.V is not (27 or 28))
        {
            throw new InvalidOperationException($"Signature recovery id {signature.RecoveryId} has no ECDSAProofSystem encoding.");
        }

        byte[] packed = new byte[SignatureLength];
        signature.Bytes.CopyTo(packed);
        packed[^1] = (byte)signature.V;
        return packed;
    }
}
