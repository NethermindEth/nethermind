// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Nethermind.Torrent;

/// <summary>A verified BEP 44 mutable DHT value, represented by its canonical bencoded bytes.</summary>
public sealed class DhtMutableItem
{
    private DhtMutableItem(byte[] publicKey, byte[] salt, long sequence, byte[] signature, byte[] value)
    {
        PublicKey = publicKey;
        Salt = salt;
        Sequence = sequence;
        Signature = signature;
        Value = value;
        Target = SHA1.HashData([.. publicKey, .. salt]);
    }

    /// <summary>Gets the Ed25519 public key that owns this item.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Gets the optional binary namespace salt.</summary>
    public byte[] Salt { get; }

    /// <summary>Gets the nonnegative per-key sequence number.</summary>
    public long Sequence { get; }

    /// <summary>Gets the Ed25519 signature over the BEP 44 payload.</summary>
    public byte[] Signature { get; }

    /// <summary>Gets the canonical bencoded value.</summary>
    public byte[] Value { get; }

    /// <summary>Gets the 20-byte DHT lookup target.</summary>
    public byte[] Target { get; }

    /// <summary>Signs a canonical bencoded value using a 32-byte Ed25519 private seed.</summary>
    public static DhtMutableItem Sign(ReadOnlySpan<byte> privateSeed, ReadOnlySpan<byte> value, long sequence, ReadOnlySpan<byte> salt = default)
    {
        if (privateSeed.Length != 32)
        {
            throw new ArgumentException("Ed25519 private seed must be 32 bytes.", nameof(privateSeed));
        }

        Validate(value, sequence, salt);
        Ed25519PrivateKeyParameters key = new(privateSeed.ToArray(), 0);
        byte[] publicKey = key.GeneratePublicKey().GetEncoded();
        byte[] payload = SigningPayload(salt, sequence, value);
        Ed25519Signer signer = new();
        signer.Init(true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        return new DhtMutableItem(publicKey, salt.ToArray(), sequence, signer.GenerateSignature(), value.ToArray());
    }

    /// <summary>Validates a signed BEP 44 value before accepting or republishing it.</summary>
    public static DhtMutableItem FromSigned(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> salt, long sequence,
        ReadOnlySpan<byte> signature, ReadOnlySpan<byte> value)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            throw new FormatException("Mutable item needs a 32-byte key and 64-byte signature.");
        }

        Validate(value, sequence, salt);
        byte[] payload = SigningPayload(salt, sequence, value);
        Ed25519Signer verifier = new();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray(), 0));
        verifier.BlockUpdate(payload, 0, payload.Length);
        if (!verifier.VerifySignature(signature.ToArray()))
        {
            throw new FormatException("Invalid BEP 44 Ed25519 signature.");
        }

        return new DhtMutableItem(publicKey.ToArray(), salt.ToArray(), sequence, signature.ToArray(), value.ToArray());
    }

    internal static byte[] SigningPayload(ReadOnlySpan<byte> salt, long sequence, ReadOnlySpan<byte> value)
    {
        string prefix = salt.IsEmpty ? string.Empty : $"4:salt{salt.Length.ToString(CultureInfo.InvariantCulture)}:";
        byte[] header = Encoding.ASCII.GetBytes($"3:seqi{sequence.ToString(CultureInfo.InvariantCulture)}e1:v");
        byte[] payload = new byte[Encoding.ASCII.GetByteCount(prefix) + salt.Length + header.Length + value.Length];
        int offset = Encoding.ASCII.GetBytes(prefix, payload);
        salt.CopyTo(payload.AsSpan(offset));
        offset += salt.Length;
        header.CopyTo(payload, offset);
        offset += header.Length;
        value.CopyTo(payload.AsSpan(offset));
        return payload;
    }

    internal static void Validate(ReadOnlySpan<byte> value, long sequence, ReadOnlySpan<byte> salt)
    {
        if (sequence < 0 || salt.Length > 64 || value.Length is 0 or > 1000)
        {
            throw new FormatException("BEP 44 sequence, salt, or value is out of range.");
        }

        _ = BencodeDocument.Decode(value, requireCanonical: true);
    }
}

/// <summary>Queries and publishes BEP 44 immutable and mutable values on the BitTorrent DHT.</summary>
public sealed class DhtItemClient : IAsyncDisposable
{
    private readonly DhtClient _client = new(new byte[20], _ => { });

    /// <summary>Retrieves an immutable bencoded value with the expected SHA-1 target.</summary>
    public Task<byte[]?> GetImmutableAsync(byte[] target, CancellationToken token)
        => _client.GetImmutableAsync(target, token);

    /// <summary>Publishes a canonical immutable bencoded value and returns acknowledged stores.</summary>
    public Task<int> PutImmutableAsync(byte[] value, CancellationToken token)
        => _client.PutImmutableAsync(value, token);

    /// <summary>Retrieves the highest valid sequence found for a public key and optional salt.</summary>
    public Task<DhtMutableItem?> GetMutableAsync(byte[] publicKey, byte[]? salt, CancellationToken token)
        => _client.GetMutableAsync(publicKey, salt ?? [], token);

    /// <summary>Publishes a signed value, optionally requiring the existing sequence to match a compare-and-swap value.</summary>
    public Task<int> PutMutableAsync(DhtMutableItem item, long? compareAndSwap, CancellationToken token)
        => _client.PutMutableAsync(item, compareAndSwap, token);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
