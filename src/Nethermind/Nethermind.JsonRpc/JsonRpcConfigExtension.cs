// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Nethermind.JsonRpc
{
    public static class JsonRpcConfigExtension
    {
        public static void EnableModules(this IJsonRpcConfig config, params string[] modules)
        {
            HashSet<string> enabledModules = config.EnabledModules.ToHashSet();
            for (int i = 0; i < modules.Length; i++)
            {
                enabledModules.Add(modules[i]);
            }
            config.EnabledModules = enabledModules.ToArray();
        }

        /// <summary>
        /// Constructs a <see cref="CancellationTokenSource"/> that timeouts after <see cref="IJsonRpcConfig.Timeout"/>
        /// if <see cref="Debugger.IsAttached"/> is false.
        /// </summary>
        public static CancellationTokenSource BuildTimeoutCancellationToken(this IJsonRpcConfig config)
        {
            return Debugger.IsAttached ? new CancellationTokenSource() : new CancellationTokenSource(config.Timeout);
        }
    }
}
