// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MongoDB.Driver;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public interface IMongoProvider
    {
        IMongoDatabase? GetDatabase();
    }
}
