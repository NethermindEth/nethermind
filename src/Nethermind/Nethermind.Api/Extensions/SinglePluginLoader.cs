// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;

namespace Nethermind.Api.Extensions
{
    /// <summary>
    /// This class is introduced for easier testing of the plugins under construction - it allows to load a plugin
    /// directly from the current solution
    /// </summary>
    /// <typeparam name="T">Type of the plugin to load</typeparam>
    public class SinglePluginLoader<T> : IPluginLoader where T : INethermindPlugin
    {
        private SinglePluginLoader() { }

        public static IPluginLoader Instance { get; } = new SinglePluginLoader<T>();

        public IEnumerable<Type> PluginTypes => Enumerable.Repeat(typeof(T), 1);

        public void Load(ILogManager logManager) { }

        public void OrderPlugins(IPluginConfig pluginConfig) { }
    }
}
