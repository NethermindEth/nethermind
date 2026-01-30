// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging;

public static class LogManagerExtensions
{
    public static ILogger GetTypeLogger(this ILogManager logManager, string typeName) => logManager.GetLogger(ILogger.GetTypeName(typeName));
}
