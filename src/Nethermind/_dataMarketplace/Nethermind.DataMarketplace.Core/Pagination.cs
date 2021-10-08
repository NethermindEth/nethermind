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

            int totalPages = (int) Math.Ceiling((double) totalResults / results);
            int skip = (page - 1) * results;
            List<T> items = values.Skip(skip).Take(results).ToList();

            return PagedResult<T>.Create(items, page, results, totalPages, totalResults);
        }
    }
}