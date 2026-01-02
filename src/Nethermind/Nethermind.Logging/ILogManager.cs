// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging
{
    public interface ILogManager
    {
        ILogger GetClassLogger<T>();
        ILogger GetClassLogger([CallerFilePath] string filePath = "");
        ILogger GetLogger(string loggerName);

        void SetGlobalVariable(string name, object value) { }
    }
}
