// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

public static class ErrorMessages
{
    /// <summary>
    /// EIP-4444 message for <see cref="ErrorCodes.PrunedHistoryUnavailable"/>
    /// </summary>
    public const string PrunedHistoryUnavailable = "Pruned history unavailable";

    public static string MethodNotFound(string methodName) => $"the method {methodName} does not exist/is not available";
}
