using System;

namespace Nethermind.Crypto;

/// <summary>
/// Span wrapper upon SecP256k1. Try to avoid allocations if given span is of Keccak size.
/// </summary>
public static class SpanSecP256k1
{
    [ThreadStatic] private static byte[]? _signMessageHash;
    [ThreadStatic] private static byte[]? _signPrivateKey;

    public static byte[]? SignCompact(ReadOnlySpan<byte> messageHash, ReadOnlySpan<byte> privateKey, out int recoveryId)
    {
        byte[] messageHashArray;
        if (messageHash.Length == 32)
        {
            _signMessageHash ??= new byte[32];
            messageHash.CopyTo(_signMessageHash);
            messageHashArray = _signMessageHash;
        }
        else
        {
            // Why? Dont know...
            messageHashArray = messageHash.ToArray();
        }

        byte[] privateKeyArray;
        if (privateKey.Length == 32)
        {
            _signPrivateKey ??= new byte[32];
            privateKey.CopyTo(_signPrivateKey);
            privateKeyArray = _signPrivateKey;
        }
        else
        {
            // Why? Dont know...
            privateKeyArray = privateKey.ToArray();
        }

        return SecP256k1.SignCompact(messageHashArray, privateKeyArray, out recoveryId);
    }

    [ThreadStatic] private static byte[]? _recoverMessageHash;

    public static bool RecoverKeyFromCompact(Span<byte> publicKey, ReadOnlySpan<byte> messageHash, ReadOnlySpan<byte> signature, int recoveryId, bool compressed)
    {
        byte[] messageHashArray;
        if (messageHash.Length == 32)
        {
            _recoverMessageHash ??= new byte[32];
            messageHash.CopyTo(_recoverMessageHash);
            messageHashArray = _recoverMessageHash;
        }
        else
        {
            // Why? Dont know...
            messageHashArray = messageHash.ToArray();
        }

        return SecP256k1.RecoverKeyFromCompact(publicKey, messageHashArray, signature, recoveryId, compressed);
    }
}
