// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface ILogManager
    {
        ILogger GetClassLogger<T>();
        ILogger GetClassLogger(Type type) => GetLogger(type.FullName?.Replace("Nethermind.", string.Empty) ?? type.Name);
        ILogger GetLogger(string loggerName);

        void SetGlobalVariable(string name, object value) { }
    }
}
