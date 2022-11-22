// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using MongoDB.Driver;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public class NullMongoProvider : IMongoProvider
    {
        private NullMongoProvider()
        {
        }

        public static IMongoProvider Instance { get; } = new NullMongoProvider();

        public IMongoDatabase GetDatabase()
        {
            throw new NotSupportedException("Mongo provider is null");
        }
    }
}
