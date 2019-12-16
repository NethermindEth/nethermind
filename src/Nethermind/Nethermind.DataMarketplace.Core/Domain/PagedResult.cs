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

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class PagedResult<T> : PagedResultBase
    {
        public bool IsEmpty => Items == null || !Items.Any();
        public IReadOnlyList<T> Items { get; }

        private PagedResult()
        {
            Items = Array.Empty<T>();
        }

        private PagedResult(IReadOnlyList<T> items, int page, int results, int totalPages, long totalResults) : base(
            page, results, totalPages, totalResults)
        {
            Items = items;
        }

        public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int results, int totalPages,
            long totalResults) => new PagedResult<T>(items, page, results, totalPages, totalResults);

        public static PagedResult<T> From(PagedResultBase result, IReadOnlyList<T> items)
            => new PagedResult<T>(items, result.Page, result.Results,
                result.TotalPages, result.TotalResults);

        public static PagedResult<T> Empty => new PagedResult<T>();
    }
}