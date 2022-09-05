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
//

using Nethermind.Config;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public interface ITraceStoreConfig : IConfig
{
    [ConfigItem(Description = "Defines whether the TraceStore plugin is enabled, if 'true' traces will come from DB if possible.", DefaultValue = "false")]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "Defines how many blocks counting from head are kept in the TraceStore, if '0' all traces of processed blocks will be kept.", DefaultValue = "10000")]
    public int BlocksToKeep { get; set; }

    [ConfigItem(Description = "Defines what kind of traces are saved and kept in TraceStore.", DefaultValue = "Trace and Rewards")]
    public ParityTraceTypes TraceTypes { get; set; }
}
