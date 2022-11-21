// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
