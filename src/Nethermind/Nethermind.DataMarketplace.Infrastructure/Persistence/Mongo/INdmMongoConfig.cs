// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
