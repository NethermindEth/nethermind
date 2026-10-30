// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Utilities;

internal static class LogEndpoint
{
    public static string Address(string? value) => SensitiveLogMasking.Enabled ? "[redacted]" : value ?? "unknown";

    public static string Url(string? value) => SensitiveLogMasking.SafeUrl(value);
}
