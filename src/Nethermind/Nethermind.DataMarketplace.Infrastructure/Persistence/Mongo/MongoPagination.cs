// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Mongo
{
    public static class MongoPagination
    {
        public static Task<PagedResult<T>> PaginateAsync<T>(this IMongoQueryable<T> values, PagedQueryBase query)
            => values.PaginateAsync(query.Page, query.Results);

        public static async Task<PagedResult<T>> PaginateAsync<T>(this IMongoQueryable<T> values, int page = 1,
            int results = 10)
        {
            var totalResults = await values.CountAsync();
            if (totalResults == 0)
            {
                return PagedResult<T>.Empty;
            }

            if (page <= 0)
            {
                page = 1;
            }

            if (results <= 0)
            {
                results = 10;
            }

            var totalPages = (int)Math.Ceiling((double)totalResults / results);
            var skip = (page - 1) * results;
            var items = await values.Skip(skip).Take(results).ToListAsync();

            return PagedResult<T>.Create(items, page, results, totalPages, totalResults);
        }
    }
}
