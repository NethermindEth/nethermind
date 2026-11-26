// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Nethermind.Zkvm.Abstractions;

/// <summary>
/// Provides <see href="https://github.com/eth-act/zkvm-standards/tree/main/standards/c-interface-accelerators">
/// zkVM cryptographic accelerators</see>.
/// </summary>
public static partial class Accelerators
{
    /// <summary>
    /// Performs BLAKE2f compression.
    /// </summary>
    /// <param name="rounds">The number of rounds.</param>
    /// <param name="state">The state vector.</param>
    /// <param name="message">The message block.</param>
    /// <param name="offset">The offset counters.</param>
    /// <param name="finalBlock">The final block indicator.</param>
    /// <exception cref="ArgumentOutOfRangeException">The <c>state</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>message</c> must be 128 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>offset</c> must be 16 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static void Blake2F(
        uint rounds,
        Span<byte> state,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> offset,
        byte finalBlock)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(state.Length, 64, nameof(state));
        ArgumentOutOfRangeException.ThrowIfNotEqual(message.Length, 128, nameof(message));
        ArgumentOutOfRangeException.ThrowIfNotEqual(offset.Length, 16, nameof(offset));

        Status status = zkvm_blake2f(rounds, state, message, offset, finalBlock);

        ThrowIfFailed(status, nameof(zkvm_blake2f));
    }

    /// <summary>
    /// Performs BLS12-381 G1 point addition.
    /// </summary>
    /// <param name="p1">The first point <c>(x || y)</c>.</param>
    /// <param name="p2">The second point <c>(x || y)</c>.</param>
    /// <param name="result">The resulting point <c>(x || y)</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>p1</c> must be 96 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>p2</c> must be 96 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 96 bytes long.</exception>
    public static Status Bls12381G1Add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 96, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 96, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));

        return zkvm_bls12_g1_add(p1, p2, result);
    }

    /// <summary>
    /// Performs BLS12-381 G1 multi-scalar multiplication.
    /// </summary>
    /// <param name="pairs">The array of point-scalar pairs.</param>
    /// <param name="numPairs">The number of point-scalar pairs.</param>
    /// <param name="result">The resulting point.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>pairs</c> must be <c>128 * numPairs</c> bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 96 bytes long.</exception>
    public static Status Bls12381G1Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 128 * numPairs, nameof(pairs));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));

        return zkvm_bls12_g1_msm(pairs, numPairs, result);
    }

    /// <summary>
    /// Performs BLS12-381 G2 point addition.
    /// </summary>
    /// <param name="p1">The first point <c>(x || y)</c>.</param>
    /// <param name="p2">The second point <c>(x || y)</c>.</param>
    /// <param name="result">The resulting point <c>(x || y)</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>p1</c> must be 192 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>p2</c> must be 192 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 192 bytes long.</exception>
    public static Status Bls12381G2Add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 192, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 192, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));

        return zkvm_bls12_g2_add(p1, p2, result);
    }

    /// <summary>
    /// Performs BLS12-381 G2 multi-scalar multiplication.
    /// </summary>
    /// <param name="pairs">The array of point-scalar pairs.</param>
    /// <param name="numPairs">The number of point-scalar pairs.</param>
    /// <param name="result">The resulting point.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>pairs</c> must be <c>224 * numPairs</c> bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 192 bytes long.</exception>
    public static Status Bls12381G2Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 224 * numPairs, nameof(pairs));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));

        return zkvm_bls12_g2_msm(pairs, numPairs, result);
    }

    /// <summary>
    /// Checks if the BLS12-381 pairing equation holds for the given points.
    /// </summary>
    /// <param name="pairs">The array of G1-G2 point pairs.</param>
    /// <param name="numPairs">The number of point pairs.</param>
    /// <param name="verified"><c>true</c> if the pairing equation holds; otherwise, <c>false</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>pairs</c> must be <c>288 * numPairs</c> bytes long.</exception>
    public static Status Bls12381Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 288 * numPairs, nameof(pairs));

        return zkvm_bls12_pairing(pairs, numPairs, out verified);
    }

    /// <summary>
    /// Maps a field element to a BLS12-381 G1 point.
    /// </summary>
    /// <param name="fieldElement">The Fp element.</param>
    /// <param name="result">The resulting point <c>(x || y)</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>fieldElement</c> must be 48 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 96 bytes long.</exception>
    public static Status Bls12381MapFpToG1(ReadOnlySpan<byte> fieldElement, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldElement.Length, 48, nameof(fieldElement));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));

        return zkvm_bls12_map_fp_to_g1(fieldElement, result);
    }

    /// <summary>
    /// Maps a field element to a BLS12-381 G2 point.
    /// </summary>
    /// <param name="fieldElement">The Fp2 element.</param>
    /// <param name="result">The resulting point <c>(x || y)</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>fieldElement</c> must be 96 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 192 bytes long.</exception>
    public static Status Bls12381MapFp2ToG2(ReadOnlySpan<byte> fieldElement, Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldElement.Length, 96, nameof(fieldElement));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));

        return zkvm_bls12_map_fp2_to_g2(fieldElement, result);
    }

    /// <summary>
    /// Performs BN254 G1 point addition.
    /// </summary>
    /// <param name="p1">The first point <c>(x || y)</c>.</param>
    /// <param name="p2">The second point <c>(x || y)</c>.</param>
    /// <param name="result">The resulting point <c>(x || y)</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>p1</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>p2</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 64 bytes long.</exception>
    public static Status BN254G1Add(
        ReadOnlySpan<byte> p1,
        ReadOnlySpan<byte> p2,
        Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 64, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 64, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 64, nameof(result));

        return zkvm_bn254_g1_add(p1, p2, result);
    }

    /// <summary>
    /// Performs BN254 G1 scalar multiplication.
    /// </summary>
    /// <param name="point">The input point <c>(x || y)</c>.</param>
    /// <param name="scalar">The scalar.</param>
    /// <param name="result">The resulting point <c>(x || y)</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>point</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>scalar</c> must be 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>result</c> must be 64 bytes long.</exception>
    public static Status BN254G1Mul(
        ReadOnlySpan<byte> point,
        ReadOnlySpan<byte> scalar,
        Span<byte> result)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(point.Length, 64, nameof(point));
        ArgumentOutOfRangeException.ThrowIfNotEqual(scalar.Length, 32, nameof(scalar));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 64, nameof(result));

        return zkvm_bn254_g1_mul(point, scalar, result);
    }

    /// <summary>
    /// Checks if the BN254 pairing equation holds for the given points.
    /// </summary>
    /// <param name="pairs">The array of G1-G2 point pairs.</param>
    /// <param name="numPairs">The number of point pairs.</param>
    /// <param name="verified"><c>true</c> if the pairing equation holds; otherwise, <c>false</c>.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>pairs</c> buffer must be <c>192 * numPairs</c> bytes long.</exception>
    public static Status BN254Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 192 * numPairs, nameof(pairs));

        return zkvm_bn254_pairing(pairs, numPairs, out verified);
    }

    /// <summary>
    /// Computes the hash of data using the Keccak-256 algorithm.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <param name="output">The buffer to receive the hash value.</param>
    /// <exception cref="ArgumentOutOfRangeException">The <c>output</c> must be 32 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static void Keccak256(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, 32, nameof(output));

        Status status = zkvm_keccak256(data, (nuint)data.Length, output);

        ThrowIfFailed(status, nameof(zkvm_keccak256));
    }

    /// <summary>
    /// Verifies a KZG proof for point evaluation.
    /// </summary>
    /// <param name="commitment">The KZG commitment.</param>
    /// <param name="z">The evaluation point.</param>
    /// <param name="y">The claimed evaluation.</param>
    /// <param name="proof">The KZG proof.</param>
    /// <returns><c>true</c> if the proof is valid; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>commitment</c> buffer must be 48 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>z</c> buffer must be 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>y</c> buffer must be 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>proof</c> buffer must be 48 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static bool KzgPointEval(
        ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> z,
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> proof)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(commitment.Length, 48, nameof(commitment));
        ArgumentOutOfRangeException.ThrowIfNotEqual(z.Length, 32, nameof(z));
        ArgumentOutOfRangeException.ThrowIfNotEqual(y.Length, 32, nameof(y));
        ArgumentOutOfRangeException.ThrowIfNotEqual(proof.Length, 48, nameof(proof));

        Status status = zkvm_kzg_point_eval(commitment, z, y, proof, out bool verified);

        ThrowIfFailed(status, nameof(zkvm_kzg_point_eval));

        return verified;
    }

    /// <summary>
    /// Computes <c>(base^exp) % modulus</c> for arbitrary precision integers.
    /// </summary>
    /// <param name="base">The base, in bytes.</param>
    /// <param name="exp">The exponent value, in bytes.</param>
    /// <param name="modulus">The modulus, in bytes.</param>
    /// <param name="output">The buffer to receive the result value.</param>
    /// <exception cref="ArgumentOutOfRangeException">The <c>output</c> length must be equal to the <c>modulus</c> length.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static void ModExp(
        ReadOnlySpan<byte> @base,
        ReadOnlySpan<byte> exp,
        ReadOnlySpan<byte> modulus,
        Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, modulus.Length, nameof(output));

        Status status = zkvm_modexp(
            @base,
            (nuint)@base.Length,
            exp,
            (nuint)exp.Length,
            modulus,
            (nuint)modulus.Length,
            output
        );

        ThrowIfFailed(status, nameof(zkvm_modexp));
    }

    /// <summary>
    /// Computes the hash of data using the RIPEMD-160 algorithm.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <param name="output">The buffer to receive the hash value (20-byte hash, right-aligned in a 32-byte buffer).</param>
    /// <exception cref="ArgumentOutOfRangeException">The <c>output</c> must be 32 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static void Ripemd160(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, 32, nameof(output));

        Status status = zkvm_ripemd160(data, (nuint)data.Length, output);

        ThrowIfFailed(status, nameof(zkvm_ripemd160));
    }

    /// <summary>
    /// Recovers public key from signature.
    /// </summary>
    /// <param name="msg">The message hash.</param>
    /// <param name="sig">The signature <c>(r || s)</c>.</param>
    /// <param name="recid">The recovery id.</param>
    /// <param name="output">The buffer to receive the public key.</param>
    /// <returns>The status of the operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>msg</c> must be 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>sig</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>output</c> must be 64 bytes long.</exception>
    public static Status SecP256k1Recover(
        ReadOnlySpan<byte> msg,
        ReadOnlySpan<byte> sig,
        byte recid,
        Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(msg.Length, 32, nameof(msg));
        ArgumentOutOfRangeException.ThrowIfNotEqual(sig.Length, 64, nameof(sig));
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, 64, nameof(output));

        return zkvm_secp256k1_ecrecover(msg, sig, recid, output);
    }

    /// <summary>
    /// Verifies an ECDSA signature on the SecP256k1 curve.
    /// </summary>
    /// <param name="msg">The message hash.</param>
    /// <param name="sig">The signature <c>(r || s)</c>.</param>
    /// <param name="pubkey">The uncompressed public key <c>(x || y)</c>.</param>
    /// <returns><c>true</c> if signature is valid; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>msg</c> must be 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>sig</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>pubkey</c> must be 64 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static bool SecP256k1Verify(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> sig, ReadOnlySpan<byte> pubkey)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(msg.Length, 32, nameof(msg));
        ArgumentOutOfRangeException.ThrowIfNotEqual(sig.Length, 64, nameof(sig));
        ArgumentOutOfRangeException.ThrowIfNotEqual(pubkey.Length, 64, nameof(pubkey));

        Status status = zkvm_secp256k1_verify(msg, sig, pubkey, out bool verified);

        ThrowIfFailed(status, nameof(zkvm_secp256k1_verify));

        return verified;
    }

    /// <summary>
    /// Verifies an ECDSA signature on the SecP256r1 curve.
    /// </summary>
    /// <param name="msg">The message hash.</param>
    /// <param name="sig">The signature <c>(r || s)</c>.</param>
    /// <param name="pubkey">The uncompressed public key <c>(x || y)</c>.</param>
    /// <returns><c>true</c> if signature is valid; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The <c>msg</c> must be 32 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>sig</c> must be 64 bytes long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The <c>pubkey</c> must be 64 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static bool SecP256r1Verify(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> sig, ReadOnlySpan<byte> pubkey)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(msg.Length, 32, nameof(msg));
        ArgumentOutOfRangeException.ThrowIfNotEqual(sig.Length, 64, nameof(sig));
        ArgumentOutOfRangeException.ThrowIfNotEqual(pubkey.Length, 64, nameof(pubkey));

        Status status = zkvm_secp256r1_verify(msg, sig, pubkey, out bool verified);

        ThrowIfFailed(status, nameof(zkvm_secp256r1_verify));

        return verified;
    }

    /// <summary>
    /// Computes the hash of data using the SHA-256 algorithm.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <param name="output">The buffer to receive the hash value.</param>
    /// <exception cref="ArgumentOutOfRangeException">The <c>output</c> must be 32 bytes long.</exception>
    /// <exception cref="CryptographicException">Operation failed.</exception>
    public static void Sha256(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, 32, nameof(output));

        Status status = zkvm_sha256(data, (nuint)data.Length, output);

        ThrowIfFailed(status, nameof(zkvm_sha256));
    }

    private static void ThrowIfFailed(Status status, string methodName)
    {
        if (status != Status.OK)
            throw new CryptographicException($"{methodName} failed. Status: {status}");
    }

    /// <summary>
    /// Represents the status of an accelerator operation.
    /// </summary>
    public enum Status // zkvm_status
    {
        /// <summary>
        /// The operation completed successfully.
        /// </summary>
        OK = 0, // ZKVM_EOK
        /// <summary>
        /// The operation failed.
        /// </summary>
        Fail = -1 // ZKVM_EFAIL
    }
}
