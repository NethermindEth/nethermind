// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Utilities;

/// <summary>
/// Computes versioned hashes from KZG commitments for blob transactions (EIP-4844)
/// </summary>
public class BlobHashComputer(ILogManager logManager)
{
    /// <summary>
    /// Size of a KZG commitment in bytes
    /// </summary>
    public const int BytesPerCommitment = 48;

    /// <summary>
    /// Size of a blob versioned hash in bytes
    /// </summary>
    public const int BytesPerVersionedHash = 32;

    /// <summary>
    /// Version byte for KZG commitments (EIP-4844)
    /// </summary>
    public const byte KzgVersionByte = 0x01;

    private readonly ILogger _logger = logManager?.GetClassLogger<BlobHashComputer>() ?? throw new ArgumentNullException(nameof(logManager));

    /// <summary>
    /// Computes a versioned hash from a single KZG commitment.
    /// Algorithm: SHA256(commitment), then replace first byte with version byte (0x01)
    /// </summary>
    /// <param name="commitmentHex">The KZG commitment as a hex string (0x-prefixed, 48 bytes)</param>
    /// <returns>The versioned hash as a hex string (0x-prefixed, 32 bytes), or null if invalid</returns>
    public string? ComputeVersionedHash(string commitmentHex)
    {
        if (string.IsNullOrEmpty(commitmentHex))
        {
            _logger.Warn("Cannot compute versioned hash from null or empty commitment");
            return null;
        }

        try
        {
            byte[] commitment = Bytes.FromHexString(commitmentHex);

            if (commitment.Length != BytesPerCommitment)
            {
                _logger.Warn($"Invalid commitment length: {commitment.Length} bytes (expected {BytesPerCommitment})");
                return null;
            }

            // Compute SHA256 hash of the commitment
            byte[] hash = SHA256.HashData(commitment);

            // Replace the first byte with the KZG version byte
            hash[0] = KzgVersionByte;

            string versionedHash = Bytes.ToHexString(hash, withZeroX: true);
            _logger.Debug($"Computed versioned hash: {versionedHash} from commitment: {commitmentHex[..Math.Min(20, commitmentHex.Length)]}...");

            return versionedHash;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Error computing versioned hash from commitment: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Computes versioned hashes from an array of KZG commitments in a blobsBundle
    /// </summary>
    /// <param name="blobsBundle">The blobsBundle JsonObject from a getPayload response</param>
    /// <returns>A JsonArray of versioned hash hex strings</returns>
    public JsonArray ComputeVersionedHashes(JsonObject? blobsBundle)
    {
        JsonArray result = [];

        if (blobsBundle is null)
        {
            _logger.Debug("No blobsBundle provided, returning empty versioned hashes array");
            return result;
        }

        if (blobsBundle["commitments"] is not JsonArray commitments || commitments.Count == 0)
        {
            _logger.Debug("No commitments in blobsBundle, returning empty versioned hashes array");
            return result;
        }

        _logger.Info($"Processing block with {commitments.Count} blobs");

        int successCount = 0;
        foreach (JsonNode? commitment in commitments)
        {
            string? commitmentHex = commitment?.ToString();
            if (string.IsNullOrEmpty(commitmentHex))
            {
                _logger.Warn("Skipping null or empty commitment in blobsBundle");
                continue;
            }

            string? versionedHash = ComputeVersionedHash(commitmentHex);
            if (versionedHash is not null)
            {
                result.Add(versionedHash);
                successCount++;
            }
        }

        _logger.Info($"Computed {successCount} versioned hashes from blobsBundle");

        return result;
    }

    /// <summary>
    /// Extracts blob versioned hashes from the params array of a newPayloadV3/V4 request
    /// </summary>
    /// <param name="requestParams">The params JsonArray from a newPayload request</param>
    /// <returns>A JsonArray of versioned hash hex strings, or empty array if not present</returns>
    public static JsonArray ExtractBlobVersionedHashes(JsonArray? requestParams)
    {
        // blobVersionedHashes is the second parameter (index 1) in newPayloadV3/V4
        if (requestParams is null || requestParams.Count < 2)
        {
            return [];
        }

        JsonNode? blobHashes = requestParams[1];

        // Handle null or JsonValueKind.Null
        if (blobHashes is null || blobHashes.GetValueKind() == JsonValueKind.Null)
        {
            return [];
        }

        // Should be a JsonArray
        if (blobHashes is JsonArray hashArray)
        {
            return hashArray;
        }

        return [];
    }

    /// <summary>
    /// Converts a JsonArray of versioned hash hex strings to a string array
    /// </summary>
    /// <param name="hashArray">JsonArray of versioned hashes</param>
    /// <returns>String array of versioned hashes</returns>
    public static string[] ToStringArray(JsonArray? hashArray)
    {
        if (hashArray is null || hashArray.Count == 0)
        {
            return [];
        }

        List<string> result = new(hashArray.Count);
        foreach (JsonNode? token in hashArray)
        {
            string? value = token?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                result.Add(value);
            }
        }
        return result.ToArray();
    }
}
