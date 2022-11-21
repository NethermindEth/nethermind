// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class NdmMongoConfig : INdmMongoConfig
    {
        public string? ConnectionString { get; set; } = "mongodb://localhost:27017";
        public string Database { get; set; } = "ndm";
        public bool LogQueries { get; set; } = false;
    }
}
