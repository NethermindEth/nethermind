// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

public class EthereumEcdsa(ulong chainId) : Ecdsa, IEthereumEcdsa
{
    public ulong ChainId => chainId;

    public Address? RecoverAddress(Signature signature, in ValueHash256 message)
    {
#if ZK_EVM
        Span<byte> address = stackalloc byte[32];
        byte success;

        unsafe
        {
            fixed (byte* sig = &MemoryMarshal.GetReference(signature.Bytes))
            fixed (byte* msg = &MemoryMarshal.GetReference(message.Bytes))
            fixed (byte* output = &MemoryMarshal.GetReference(address))
                success = ZiskBindings.Crypto.secp256k1_ecdsa_address_recover_c(sig, signature.RecoveryId, msg, output);
        }
        
        return success == 0 ? new(address[12..]) : null;
#else
        Span<byte> publicKey = stackalloc byte[65];
        bool success = RecoverAddressRaw(
            signature.Bytes,
            signature.RecoveryId,
            message.Bytes,
            publicKey
            );
        
        return success ? PublicKey.ComputeAddress(publicKey[1..]) : null;
#endif
    }

    public static bool RecoverAddressRaw(
        ReadOnlySpan<byte> signature64,
        byte recoveryId,
        ReadOnlySpan<byte> message,
        Span<byte> publicKey65) =>
        SecP256k1.RecoverKeyFromCompact(publicKey65, message, signature64, recoveryId, false);
}
