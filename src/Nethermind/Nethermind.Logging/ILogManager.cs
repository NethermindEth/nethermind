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
