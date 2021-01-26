//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
