// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging;

public partial interface ILogManager
{
    ILogger GetLogger(string loggerName);

    void SetGlobalVariable(string name, object? value) { }

    static string GetLoggerName(Type type) => (type.FullName ?? type.Name).Replace("Nethermind.", string.Empty);
}

public static class LogManagerExtensions
{
    public static ILogger GetClassLogger(this ILogManager logManager, Type type)
#if ZK_EVM
        // zkEVM logging is a no-op, so the name is irrelevant and reading it costs a reflection metadata lookup.
        => logManager.GetLogger(string.Empty);
#else
        => logManager.GetLogger(ILogManager.GetLoggerName(type));
#endif
}
