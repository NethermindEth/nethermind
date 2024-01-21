// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface ILogManager
    {
        Logger GetClassLogger(Type type);
        Logger GetClassLogger<T>();
        Logger GetClassLogger();
        Logger GetLogger(string loggerName);

        void SetGlobalVariable(string name, object value) { }
    }
}
