// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using BigInteger = Org.BouncyCastle.Math.BigInteger;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;
using PublicKey = Nethermind.Core.Crypto.PublicKey;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>libp2p TLS peer authentication with a secp256k1 node key, as the EIP-8437 ethp2p binding requires.</summary>
/// <remarks>
/// Each endpoint presents one self-signed certificate whose ephemeral P-256 key is unrelated to the node key; the node key
/// signs <c>"libp2p-tls-handshake:" || SubjectPublicKeyInfo</c> in the extension <c>1.3.6.1.4.1.53594.1.1</c>
/// (libp2p specs <c>tls/tls.md</c>, peer authentication). Only secp256k1 node keys are accepted: they are the peer's RLPx
/// identity.
/// </remarks>
internal static class LeanEthp2pIdentity
{
    public const string ExtensionOid = "1.3.6.1.4.1.53594.1.1";
    private const string BasicConstraintsOid = "2.5.29.19";
    private const string KeyUsageOid = "2.5.29.15";
    private const ulong Secp256k1KeyType = 2;
    private static readonly byte[] SignaturePrefix = "libp2p-tls-handshake:"u8.ToArray();
    private static readonly Asn1Tag ExplicitVersion = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag IssuerUniqueId = new(TagClass.ContextSpecific, 1);
    private static readonly Asn1Tag SubjectUniqueId = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag ExtensionsTag = new(TagClass.ContextSpecific, 3, isConstructed: true);
    private static readonly ECDomainParameters Secp256k1;
    private static readonly BigInteger HalfOrder;

    static LeanEthp2pIdentity()
    {
        X9ECParameters curve = SecNamedCurves.GetByName("secp256k1");
        Secp256k1 = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        HalfOrder = curve.N.ShiftRight(1);
    }

    /// <summary>Creates a self-signed certificate binding a fresh P-256 key to <paramref name="nodeKey"/>.</summary>
    public static X509Certificate2 CreateCertificate(PrivateKey nodeKey)
    {
        using ECDsa certificateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = Sign(nodeKey, [.. SignaturePrefix, .. certificateKey.ExportSubjectPublicKeyInfo()]);
        AsnWriter extension = new(AsnEncodingRules.DER);
        using (extension.PushSequence())
        {
            extension.WriteOctetString(EncodePublicKey(nodeKey.CompressedPublicKey.Bytes));
            extension.WriteOctetString(signature);
        }

        CertificateRequest request = new($"SERIALNUMBER={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}", certificateKey,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension(ExtensionOid, extension.Encode(), critical: false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddHours(-1), now.AddYears(100));
        // Reloaded from PKCS#12 so that TLS backends which need a persisted, exportable key can use it; msquic on macOS
        // re-exports the key as PKCS#12.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Authenticates a peer certificate and returns its secp256k1 node key.</summary>
    /// <param name="certificate">The DER certificate the peer presented.</param>
    /// <param name="now">The time at which the certificate must be valid.</param>
    /// <param name="nodeKey">The authenticated node key.</param>
    /// <param name="error">Why authentication failed.</param>
    public static bool TryAuthenticate(ReadOnlyMemory<byte> certificate, DateTimeOffset now, [NotNullWhen(true)] out PublicKey? nodeKey,
        [NotNullWhen(false)] out string? error)
    {
        nodeKey = null;
        try
        {
            error = Authenticate(certificate, now, out nodeKey);
        }
        catch (Exception exception)
        {
            // The certificate is attacker-controlled and the parsers and signers throw many exception types: none may escape.
            error = $"malformed certificate: {exception.Message}";
        }
        return error is null;
    }

    private static string? Authenticate(ReadOnlyMemory<byte> der, DateTimeOffset now, out PublicKey? nodeKey)
    {
        nodeKey = null;
        AsnReader outer = new(der, AsnEncodingRules.DER);
        AsnReader certificate = outer.ReadSequence();
        outer.ThrowIfNotEmpty();
        ReadOnlyMemory<byte> tbs = certificate.ReadEncodedValue();
        ReadOnlyMemory<byte> signatureAlgorithm = certificate.ReadEncodedValue();
        byte[] certificateSignature = certificate.ReadBitString(out int unusedBits);
        certificate.ThrowIfNotEmpty();
        if (unusedBits != 0) return "certificate signature is not a whole number of bytes";

        AsnReader body = new AsnReader(tbs, AsnEncodingRules.DER).ReadSequence();
        if (!body.PeekTag().HasSameClassAndValue(ExplicitVersion)) return "certificate is not X.509 v3";
        AsnReader version = body.ReadSequence(ExplicitVersion);
        if (!version.TryReadInt32(out int versionNumber) || versionNumber != 2) return "certificate is not X.509 v3";
        version.ThrowIfNotEmpty();
        body.ReadIntegerBytes();
        if (!body.ReadEncodedValue().Span.SequenceEqual(signatureAlgorithm.Span)) return "inner and outer signature algorithms differ";
        body.ReadEncodedValue();
        AsnReader validity = body.ReadSequence();
        DateTimeOffset notBefore = ReadTime(validity);
        DateTimeOffset notAfter = ReadTime(validity);
        validity.ThrowIfNotEmpty();
        if (now < notBefore) return "certificate is not yet valid";
        if (now > notAfter) return "certificate has expired";
        body.ReadEncodedValue();
        ReadOnlyMemory<byte> subjectPublicKeyInfo = body.ReadEncodedValue();
        if (body.HasData && (body.PeekTag().HasSameClassAndValue(IssuerUniqueId) || body.PeekTag().HasSameClassAndValue(SubjectUniqueId)))
            return "certificate carries deprecated unique identifiers";
        if (!body.HasData) return "certificate has no extensions";
        AsnReader extensions = body.ReadSequence(ExtensionsTag).ReadSequence();
        body.ThrowIfNotEmpty();

        byte[]? signedKey = null;
        while (extensions.HasData)
        {
            AsnReader extension = extensions.ReadSequence();
            string oid = extension.ReadObjectIdentifier();
            bool critical = extension.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean) && extension.ReadBoolean();
            byte[] value = extension.ReadOctetString();
            extension.ThrowIfNotEmpty();
            if (oid == ExtensionOid)
            {
                if (signedKey is not null) return "certificate repeats the libp2p public key extension";
                signedKey = value;
            }
            else if (critical && oid is not (BasicConstraintsOid or KeyUsageOid)) return $"certificate has an unknown critical extension {oid}";
        }
        if (signedKey is null) return "certificate lacks the libp2p public key extension";

        if (!VerifySelfSignature(tbs, signatureAlgorithm, subjectPublicKeyInfo, certificateSignature)) return "certificate self-signature is invalid";

        AsnReader signedKeyReader = new(signedKey, AsnEncodingRules.DER);
        AsnReader signedKeySequence = signedKeyReader.ReadSequence();
        signedKeyReader.ThrowIfNotEmpty();
        byte[] publicKey = signedKeySequence.ReadOctetString();
        byte[] keySignature = signedKeySequence.ReadOctetString();
        signedKeySequence.ThrowIfNotEmpty();
        if (!TryDecodePublicKey(publicKey, out ulong keyType, out ReadOnlySpan<byte> keyData)) return "malformed libp2p public key";
        if (keyType != Secp256k1KeyType || keyData.Length != CompressedPublicKey.LengthInBytes) return "node key is not a compressed secp256k1 key";
        ECPoint point = Secp256k1.Curve.DecodePoint(keyData);
        if (!Verify(point, [.. SignaturePrefix, .. subjectPublicKeyInfo.Span], keySignature)) return "libp2p public key signature is invalid";
        nodeKey = new PublicKey(point.GetEncoded(false));
        return null;
    }

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();

    private static bool VerifySelfSignature(ReadOnlyMemory<byte> tbs, ReadOnlyMemory<byte> algorithm, ReadOnlyMemory<byte> subjectPublicKeyInfo,
        byte[] signature)
    {
        AsnReader algorithmReader = new AsnReader(algorithm, AsnEncodingRules.DER).ReadSequence();
        string oid = algorithmReader.ReadObjectIdentifier();
        // ECDSA and Ed25519 identifiers have no parameters; PKCS#1 v1.5 RSA has NULL. RSASSA-PSS is not accepted.
        if (algorithmReader.HasData) algorithmReader.ReadNull();
        algorithmReader.ThrowIfNotEmpty();
        if (oid == "1.2.840.113549.1.1.10") return false;
        AsymmetricKeyParameter key = PublicKeyFactory.CreateKey(subjectPublicKeyInfo.ToArray());
        if (!SignsWith(oid, key)) return false;
        ISigner verifier = SignerUtilities.GetSigner(oid);
        verifier.Init(false, key);
        verifier.BlockUpdate(tbs.Span);
        return verifier.VerifySignature(signature);
    }

    /// <summary>Whether a signature algorithm identifier names the family of the certificate's own key.</summary>
    /// <remarks>Signers assume their key type, so a mislabelled algorithm is rejected before one is initialized.</remarks>
    private static bool SignsWith(string oid, AsymmetricKeyParameter key) => key switch
    {
        ECPublicKeyParameters => oid.StartsWith("1.2.840.10045.4.", StringComparison.Ordinal),
        RsaKeyParameters => oid.StartsWith("1.2.840.113549.1.1.", StringComparison.Ordinal),
        Ed25519PublicKeyParameters => oid == "1.3.101.112",
        Ed448PublicKeyParameters => oid == "1.3.101.113",
        _ => false
    };

    /// <summary>Decodes the protobuf <c>PublicKey { KeyType Type = 1; bytes Data = 2; }</c>.</summary>
    private static bool TryDecodePublicKey(ReadOnlySpan<byte> encoded, out ulong keyType, out ReadOnlySpan<byte> data)
    {
        keyType = ulong.MaxValue;
        data = default;
        bool hasData = false;
        while (!encoded.IsEmpty)
        {
            if (!TryReadVarint(ref encoded, out ulong tag)) return false;
            switch (tag)
            {
                case 0x08:
                    if (!TryReadVarint(ref encoded, out keyType)) return false;
                    break;
                case 0x12:
                    if (!TryReadVarint(ref encoded, out ulong length) || length > (ulong)encoded.Length) return false;
                    data = encoded[..(int)length];
                    encoded = encoded[(int)length..];
                    hasData = true;
                    break;
                default:
                    return false;
            }
        }
        return hasData && keyType != ulong.MaxValue;
    }

    private static bool TryReadVarint(ref ReadOnlySpan<byte> encoded, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && !encoded.IsEmpty; shift += 7)
        {
            byte next = encoded[0];
            encoded = encoded[1..];
            value |= (ulong)(next & 0x7f) << shift;
            if (next < 0x80) return true;
        }
        return false;
    }

    private static byte[] EncodePublicKey(ReadOnlySpan<byte> compressed) => [0x08, (byte)Secp256k1KeyType, 0x12, (byte)compressed.Length, .. compressed];

    /// <summary>A DER ECDSA signature over SHA-256 with low S, as libp2p secp256k1 keys sign.</summary>
    internal static byte[] Sign(PrivateKey key, ReadOnlySpan<byte> message)
    {
        ECDsaSigner signer = new(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, new ECPrivateKeyParameters(new BigInteger(1, key.KeyBytes), Secp256k1));
        BigInteger[] signature = signer.GenerateSignature(SHA256.HashData(message));
        BigInteger s = signature[1].CompareTo(HalfOrder) > 0 ? Secp256k1.N.Subtract(signature[1]) : signature[1];
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteIntegerUnsigned(signature[0].ToByteArrayUnsigned());
            writer.WriteIntegerUnsigned(s.ToByteArrayUnsigned());
        }
        return writer.Encode();
    }

    internal static bool Verify(ECPoint publicKey, ReadOnlySpan<byte> message, byte[] signature)
    {
        AsnReader outer = new(signature, AsnEncodingRules.DER);
        AsnReader sequence = outer.ReadSequence();
        outer.ThrowIfNotEmpty();
        ReadOnlySpan<byte> rBytes = sequence.ReadIntegerBytes().Span;
        ReadOnlySpan<byte> sBytes = sequence.ReadIntegerBytes().Span;
        sequence.ThrowIfNotEmpty();
        // DER integers are two's complement: a set high bit is a negative, never a valid signature component.
        if (rBytes[0] >= 0x80 || sBytes[0] >= 0x80) return false;
        BigInteger r = new(1, rBytes);
        BigInteger s = new(1, sBytes);
        if (r.SignValue <= 0 || s.SignValue <= 0 || r.CompareTo(Secp256k1.N) >= 0 || s.CompareTo(Secp256k1.N) >= 0) return false;
        ECDsaSigner verifier = new();
        verifier.Init(false, new ECPublicKeyParameters(publicKey, Secp256k1));
        return verifier.VerifySignature(SHA256.HashData(message), r, s);
    }
}
