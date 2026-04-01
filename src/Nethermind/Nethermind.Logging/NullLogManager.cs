// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging
{
    public class NullLogManager : ILogManager
    {
        private NullLogManager()
        {
        }

        public static ILogManager Instance { get; } = new NullLogManager();

        public ILogger GetClassLogger<T>() => NullLogger.Instance;

#pragma warning disable CS0618 // Obsolete - kept for NativeAOT compatibility
        public ILogger GetClassLogger(string filePath) => NullLogger.Instance;
#pragma warning restore CS0618

        public ILogger GetLogger(string loggerName) => NullLogger.Instance;
    }
}
