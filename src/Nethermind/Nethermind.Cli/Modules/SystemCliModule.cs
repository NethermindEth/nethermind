// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Cli.Modules
{
    [CliModule("system")]
    public class SystemCliModule : CliModuleBase
    {
        [CliFunction("system", "getVariable")]
        public string? GetVariable(string name, string defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name.ToUpperInvariant());
            return string.IsNullOrWhiteSpace(value) ? value : defaultValue;
        }

        [CliProperty("system", "memory")]
        public string Memory(string name, string defaultValue)
        {
            return $"Allocated: {GC.GetTotalMemory(false)}, GC0: {GC.CollectionCount(0)}, GC1: {GC.CollectionCount(1)}, GC2: {GC.CollectionCount(2)}";
        }

        public SystemCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
