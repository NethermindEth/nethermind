// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging;

public partial interface ILogManager
{
    ILogger GetClassLogger<T>();
}

public static class LogManagerExtensions
{
    public static ILogger GetClassLogger(this ILogManager logManager, Type type)
        => logManager.GetLogger(ILogManager.GetLoggerName(type));
}
