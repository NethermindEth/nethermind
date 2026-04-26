// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging;

public interface ILogManager
{
    ILogger GetClassLogger<T>();

    ILogger GetLogger(string loggerName);

    void SetGlobalVariable(string name, object value) { }

    static string GetLoggerName(Type type) => (type.FullName ?? type.Name).Replace("Nethermind.", string.Empty);
}

public static class LogManagerExtensions
{
    public static ILogger GetClassLogger(this ILogManager logManager, Type type)
        => logManager.GetLogger(ILogManager.GetLoggerName(type));
}
