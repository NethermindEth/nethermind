using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core
{
    public static class Pagination
    {
        public static PagedResult<T> Paginate<T>(this IEnumerable<T> values, PagedQueryBase query)
            => Paginate(values, query.Page, query.Results);

        public static PagedResult<T> Paginate<T>(this IEnumerable<T> values, int page = 1, int results = 10)
        {
            var totalResults = values.Count();
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
            var items = values.Skip(skip).Take(results).ToList();

            return PagedResult<T>.Create(items, page, results, totalPages, totalResults);
        }
    }
}