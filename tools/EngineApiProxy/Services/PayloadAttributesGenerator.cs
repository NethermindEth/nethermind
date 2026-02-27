// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.EngineApiProxy.Config;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Services;

/// <summary>
/// Generates payload attributes for block processing
/// </summary>
public class PayloadAttributesGenerator(ProxyConfig config, ILogManager logManager)
{
    private readonly ProxyConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

    /// <summary>
    /// Generates payload attributes based on the block data
    /// </summary>
    /// <param name="blockData">Block data from execution client</param>
    /// <returns>A JObject containing the payload attributes</returns>
    public JObject GeneratePayloadAttributes(JObject blockData)
    {
        _logger.Debug("Generating payload attributes from block data");

        try
        {
            // Extract required data from block
            string timestamp = blockData["timestamp"]?.ToString() ?? "0x0";

            // Calculate next timestamp (current + offset)
            string nextTimestamp = CalculateNextTimestamp(timestamp);

            // Generate random prevRandao value
            string prevRandao = blockData["prevRandao"]?.ToString() ?? GenerateRandomPrevRandao();

            // Use configured fee recipient
            string feeRecipient = _config.DefaultFeeRecipient;

            // Generate a random parentBeaconBlockRoot (32 bytes)
            string parentBeaconBlockRoot = blockData["parentBeaconBlockRoot"]?.ToString() ?? GenerateRandomHash();

            // Create payload attributes
            var payloadAttributes = new JObject
            {
                ["timestamp"] = nextTimestamp,
                ["prevRandao"] = prevRandao,
                ["suggestedFeeRecipient"] = feeRecipient,
                ["parentBeaconBlockRoot"] = parentBeaconBlockRoot,
                ["withdrawals"] = blockData["withdrawals"] ?? new JArray() // Empty withdrawals array
            };

            _logger.Debug($"Generated payload attributes: {payloadAttributes}");
            return payloadAttributes;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error generating payload attributes: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Calculates the next block timestamp based on current timestamp and offset
    /// </summary>
    /// <param name="currentTimestamp">Current block timestamp (hex)</param>
    /// <returns>Next block timestamp (hex)</returns>
    private string CalculateNextTimestamp(string currentTimestamp)
    {
        // Parse hex timestamp to long
        long current = long.Parse(currentTimestamp.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber);

        // Add offset in seconds
        long next = current + _config.TimestampOffsetSeconds;

        // Convert back to hex with 0x prefix
        return $"0x{next:x}";
    }

    /// <summary>
    /// Generates a random prevRandao value (32 bytes)
    /// </summary>
    /// <returns>Random prevRandao as 0x-prefixed hex string</returns>
    private static string GenerateRandomPrevRandao()
    {
        return GenerateRandomHash();
    }

    /// <summary>
    /// Generates a random 32-byte hash
    /// </summary>
    /// <returns>Random hash as 0x-prefixed hex string</returns>
    private static string GenerateRandomHash()
    {
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return $"0x{BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()}";
    }
}
