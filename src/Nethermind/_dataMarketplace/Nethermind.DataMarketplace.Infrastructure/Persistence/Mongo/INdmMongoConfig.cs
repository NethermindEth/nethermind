//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    // TODO: probably can ensure non-nullability of fields with default values in configs
    public interface INdmMongoConfig : IConfig
    {
        [ConfigItem(Description = "Connection string to the Mongo database (if NdmConfig.Persistence = mongo)", DefaultValue = "mongodb://localhost:27017")]
        string? ConnectionString { get; set; }
        
        [ConfigItem(Description = "An arbitrary name of the Mongo database", DefaultValue = "ndm")]
        string Database { get; }
        
        [ConfigItem(Description = "If 'true' then it logs the queries sent to the Mongo database", DefaultValue = "false")]
        bool LogQueries { get; }
    }
}