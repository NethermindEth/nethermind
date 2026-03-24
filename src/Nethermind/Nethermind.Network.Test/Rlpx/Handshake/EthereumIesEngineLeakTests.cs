// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Collections;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx.Handshake;

/// <summary>
/// Proves that <see cref="EthereumIesEngine"/> leaks ArrayPool buffers when MAC validation
/// fails during decryption, and verifies the fix returns all rented buffers.
///
/// Uses <see cref="EciesCipher"/> as the public API entry point (with an internal
/// constructor that accepts a tracking pool) to avoid direct BouncyCastle type references,
/// which conflict with the old BouncyCastle.Crypto assembly bundled by DotNetty.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class EthereumIesEngineLeakTests
{
    private TestRandom _cryptoRandom;

    [SetUp]
    public void Setup()
    {
        _cryptoRandom = new TestRandom();
    }

    [TearDown]
    public void TearDown() => _cryptoRandom?.Dispose();

    /// <summary>
    /// BEFORE the fix: DecryptBlock rents 2 buffers (k2ABuffer for the hash digest,
    /// macOutputBuffer for the computed MAC) then throws InvalidCipherTextException
    /// on MAC mismatch before reaching the Return calls. Both buffers leak.
    ///
    /// AFTER the fix (converting raw Rent/Return to using ArrayPoolSpan): all buffers
    /// are returned via Dispose even when an exception is thrown.
    /// </summary>
    [Test]
    public void DecryptBlock_on_MAC_failure_should_not_leak_array_pool_buffers()
    {
        CountingArrayPool<byte> pool = new();
        PrivateKey privateKey = NetTestVectors.StaticKeyA;

        // Encrypt a valid message -- the encrypt path uses a separate engine instance
        // per EciesCipher.Encrypt, but we pass the tracking pool so we can verify
        // encrypt doesn't leak either.
        EciesCipher cipher = new(_cryptoRandom, pool);
        _cryptoRandom.EnqueueRandomBytes(Bytes.FromHexString("0x0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a"));
        _cryptoRandom.EnqueueRandomBytes(NetTestVectors.EphemeralKeyA.KeyBytes);
        byte[] cipherText = cipher.Encrypt(privateKey.PublicKey, [0x01, 0x02, 0x03, 0x04, 0x05], []);

        int outstandingAfterEncrypt = pool.Outstanding;
        Assert.That(outstandingAfterEncrypt, Is.EqualTo(0),
            "Encrypt should not leak buffers");

        // Corrupt the ciphertext body to force MAC validation failure during decrypt.
        // The MAC covers the cipher body, so flipping bits in the body (not the ephemeral
        // public key prefix or IV) guarantees the computed MAC won't match the stored one.
        // The ciphertext layout is: ephemeralPubKey(65) | IV(16) | cipherBody(...)
        // We corrupt bytes in the cipherBody region.
        int cipherBodyStart = 65 + 16;
        for (int i = cipherBodyStart; i < cipherBodyStart + 4 && i < cipherText.Length; i++)
        {
            cipherText[i] ^= 0xFF;
        }

        // Reset pool counters by creating a fresh one for the decrypt path,
        // so we measure exactly the decrypt leak in isolation.
        CountingArrayPool<byte> decryptPool = new();
        EciesCipher decryptCipher = new(_cryptoRandom, decryptPool);

        Assert.That(decryptPool.Outstanding, Is.EqualTo(0),
            "No buffers should be outstanding before decryption");

        // Decrypt should throw InvalidCipherTextException due to MAC mismatch.
        // Use Assert.Catch<Exception> to avoid referencing InvalidCipherTextException directly,
        // which conflicts between old BouncyCastle.Crypto (DotNetty) and new BouncyCastle.Cryptography.
        Exception ex = Assert.Catch<Exception>(() =>
            decryptCipher.Decrypt(privateKey, cipherText));
        Assert.That(ex.GetType().Name, Is.EqualTo("InvalidCipherTextException"),
            "Expected InvalidCipherTextException from MAC validation failure");

        // BEFORE fix: Outstanding == 2 (k2ABuffer + macOutputBuffer leaked in DecryptBlock)
        // AFTER fix:  Outstanding == 0 (all buffers returned via ArrayPoolSpan dispose)
        Assert.That(decryptPool.Outstanding, Is.EqualTo(0),
            $"Expected 0 outstanding rentals after MAC failure, but found {decryptPool.Outstanding}. " +
            $"Rented: {decryptPool.RentCount}, Returned: {decryptPool.ReturnCount}. " +
            "This indicates ArrayPool buffers are leaked when DecryptBlock throws on invalid MAC.");
    }

    /// <summary>
    /// Baseline: successful decryption returns all rented ArrayPool buffers.
    /// If this fails, the pool injection wiring is broken.
    /// </summary>
    [Test]
    public void DecryptBlock_on_success_should_return_all_array_pool_buffers()
    {
        CountingArrayPool<byte> pool = new();
        PrivateKey privateKey = NetTestVectors.StaticKeyA;

        EciesCipher cipher = new(_cryptoRandom, pool);
        _cryptoRandom.EnqueueRandomBytes(Bytes.FromHexString("0x0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a"));
        _cryptoRandom.EnqueueRandomBytes(NetTestVectors.EphemeralKeyA.KeyBytes);

        byte[] plaintext = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] cipherText = cipher.Encrypt(privateKey.PublicKey, plaintext, []);

        // Reset counters for decrypt measurement
        CountingArrayPool<byte> decryptPool = new();
        EciesCipher decryptCipher = new(_cryptoRandom, decryptPool);

        (bool success, byte[] result) = decryptCipher.Decrypt(privateKey, cipherText);

        Assert.That(success, Is.True, "Decryption should succeed");
        Assert.That(result, Is.EqualTo(plaintext), "Decrypted plaintext should match original");
        Assert.That(decryptPool.Outstanding, Is.EqualTo(0),
            $"All buffers should be returned after successful decryption. " +
            $"Rented: {decryptPool.RentCount}, Returned: {decryptPool.ReturnCount}");
        Assert.That(decryptPool.RentCount, Is.GreaterThan(0),
            "The tracking pool should have been used (at least one Rent call)");
    }

    /// <summary>
    /// Verifies that EncryptBlock returns all rented ArrayPool buffers.
    /// </summary>
    [Test]
    public void EncryptBlock_should_return_all_array_pool_buffers()
    {
        CountingArrayPool<byte> pool = new();
        PrivateKey privateKey = NetTestVectors.StaticKeyA;

        EciesCipher cipher = new(_cryptoRandom, pool);
        _cryptoRandom.EnqueueRandomBytes(Bytes.FromHexString("0x0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a"));
        _cryptoRandom.EnqueueRandomBytes(NetTestVectors.EphemeralKeyA.KeyBytes);

        byte[] _ = cipher.Encrypt(privateKey.PublicKey, [0x01, 0x02, 0x03, 0x04, 0x05], []);

        Assert.That(pool.Outstanding, Is.EqualTo(0),
            $"All buffers should be returned after encryption. " +
            $"Rented: {pool.RentCount}, Returned: {pool.ReturnCount}");
        Assert.That(pool.RentCount, Is.GreaterThan(0),
            "The tracking pool should have been used (at least one Rent call)");
    }
}
