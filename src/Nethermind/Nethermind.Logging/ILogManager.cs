// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging
{
    public interface ILogManager
    {
        ILogger GetClassLogger<T>();
        ILogger GetLogger(string loggerName);

        // Preserved for binary compatibility (NativeAOT/bflat RISC-V vtable layout)
        [Obsolete("Use GetClassLogger<T>() or GetClassLogger(typeof(T)) extension method")]
        ILogger GetClassLogger(string filePath) => GetLogger(filePath);

        void SetGlobalVariable(string name, object value) { }
    }

    public static class LogManagerExtensions
    {
        private static string GetLoggerName(Type type) => (type.FullName ?? type.Name).Replace("Nethermind.", string.Empty);

        public static ILogger GetClassLogger(this ILogManager logManager, Type type)
            => logManager.GetLogger(GetLoggerName(type));
    }
}
