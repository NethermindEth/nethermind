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

        public ILogger GetClassLogger(Type type) => NullLogger.Instance;

        public ILogger GetClassLogger<T>() => NullLogger.Instance;

        public ILogger GetClassLogger() => NullLogger.Instance;

        public ILogger GetLogger(string loggerName) => NullLogger.Instance;
    }
}
