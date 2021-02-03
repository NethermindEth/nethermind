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

using Nethermind.Config;

namespace Nethermind.Grpc
{
    [ConfigCategory(HiddenFromDocs = true)]
    public interface IGrpcConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then it disables gRPC protocol", DefaultValue = "false")]
        bool Enabled { get; }
        
        [ConfigItem(Description = "An address of the host under which gRPC will be running", DefaultValue = "localhost")]
        string Host { get; }
        
        [ConfigItem(Description = "Port of the host under which gRPC will be exposed", DefaultValue = "50000")]
        int Port { get; }
    }
}
