// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface ILogManager
    {
#if !ZK_EVM
        ILogger GetClassLogger<T>();
#endif
        ILogger GetLogger(string loggerName);

        void SetGlobalVariable(string name, object value) { }

        static string GetLoggerName(Type type) => (type.FullName ?? type.Name).Replace("Nethermind.", string.Empty);
    }

    public static class LogManagerExtensions
    {
#if ZK_EVM
        public static ILogger GetClassLogger<T>(this ILogManager logManager)
            => logManager.GetLogger(ILogManager.GetLoggerName(typeof(T)));
#endif

        public static ILogger GetClassLogger(this ILogManager logManager, Type type)
            => logManager.GetLogger(ILogManager.GetLoggerName(type));
    }
}
