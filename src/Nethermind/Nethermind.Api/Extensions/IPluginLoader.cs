// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Logging;

namespace Nethermind.Api.Extensions
{
    public interface IPluginLoader
    {
        IEnumerable<Type> PluginTypes { get; }

        void Load(ILogManager logManager);

        public void OrderPlugins(IPluginConfig pluginConfig);
    }
}
