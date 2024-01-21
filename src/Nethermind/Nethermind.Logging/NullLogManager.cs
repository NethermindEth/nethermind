// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public class NullLogManager : ILogManager
    {
        private NullLogManager()
        {
        }

        public static ILogManager Instance { get; } = new NullLogManager();

        public Logger GetClassLogger(Type type) => NullLogger.Instance;

        public Logger GetClassLogger<T>() => NullLogger.Instance;

        public Logger GetClassLogger() => NullLogger.Instance;

        public Logger GetLogger(string loggerName) => NullLogger.Instance;
    }
}
