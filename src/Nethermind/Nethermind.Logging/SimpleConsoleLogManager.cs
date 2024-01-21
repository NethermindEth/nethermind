// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public class SimpleConsoleLogManager : ILogManager
    {
        private SimpleConsoleLogManager()
        {
        }

        public static ILogManager Instance { get; } = new SimpleConsoleLogManager();

        public Logger GetClassLogger(Type type)
        {
            return new(SimpleConsoleLogger.Instance);
        }

        public Logger GetClassLogger<T>()
        {
            return new(SimpleConsoleLogger.Instance);
        }

        public Logger GetClassLogger()
        {
            return new(SimpleConsoleLogger.Instance);
        }

        public Logger GetLogger(string loggerName)
        {
            return new(SimpleConsoleLogger.Instance);
        }
    }
}
