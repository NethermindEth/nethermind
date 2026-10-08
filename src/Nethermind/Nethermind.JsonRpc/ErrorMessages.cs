// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

public static class ErrorMessages
{
    internal const string BlockHashAndRange = "cannot specify both BlockHash and FromBlock/ToBlock, choose one or the other";

    /// <summary>
    /// EIP-4444 message for <see cref="ErrorCodes.PrunedHistoryUnavailable"/>
    /// </summary>
    public const string PrunedHistoryUnavailable = "Pruned history unavailable";

    /// <summary>
    /// Geth-compatible message for unsubscribing an unknown subscription id, paired with <see cref="ErrorCodes.ResourceNotFound"/>
    /// </summary>
    public const string SubscriptionNotFound = "subscription not found";

    public static string MethodNotFound(string methodName) => $"the method {methodName} does not exist/is not available";
}
