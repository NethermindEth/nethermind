// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface ILogManager
    {
        ILogger GetClassLogger(Type type);
        ILogger GetClassLogger<T>();
        ILogger GetClassLogger();
        ILogger GetLogger(string loggerName);

        void SetGlobalVariable(string name, object value) { }
    }
}
