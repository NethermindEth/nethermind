using System;

namespace Nethermind.Crypto;

/// <summary>
/// Span wrapper upon SecP256k1.
/// </summary>
public static class SpanSecP256k1
{
    public static byte[]? SignCompact(ReadOnlySpan<byte> messageHash, ReadOnlySpan<byte> privateKey, out int recoveryId)
        => SecP256k1.SignCompact(messageHash, privateKey, out recoveryId);

    public static bool RecoverKeyFromCompact(Span<byte> publicKey, ReadOnlySpan<byte> messageHash, ReadOnlySpan<byte> signature, int recoveryId, bool compressed)
        => SecP256k1.RecoverKeyFromCompact(publicKey, messageHash, signature, recoveryId, compressed);
}
