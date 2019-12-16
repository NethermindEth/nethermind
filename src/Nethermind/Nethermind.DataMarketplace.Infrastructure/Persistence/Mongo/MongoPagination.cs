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

            var totalPages = (int) Math.Ceiling((double) totalResults / results);
            var skip = (page - 1) * results;
            var items = await values.Skip(skip).Take(results).ToListAsync();

            return PagedResult<T>.Create(items, page, results, totalPages, totalResults);
        }
    }
}