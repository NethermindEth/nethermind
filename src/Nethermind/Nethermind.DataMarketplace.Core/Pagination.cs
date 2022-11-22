// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core
{
    public static class Pagination
    {
        public static PagedResult<T> Paginate<T>(this T[] values, PagedQueryBase query)
            => Paginate(values, query.Page, query.Results);

        public static PagedResult<T> Paginate<T>(this T[] values, int page = 1, int results = 10)
        {
            int totalResults = values.Count();
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

            int totalPages = (int)Math.Ceiling((double)totalResults / results);
            int skip = (page - 1) * results;
            List<T> items = values.Skip(skip).Take(results).ToList();

            return PagedResult<T>.Create(items, page, results, totalPages, totalResults);
        }
    }
}
