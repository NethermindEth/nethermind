// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class PagedResultBase
    {
        public int Page { get; }
        public int Results { get; }
        public int TotalPages { get; }
        public long TotalResults { get; }

        protected PagedResultBase()
        {
        }

        protected PagedResultBase(int page, int results, int totalPages, long totalResults)
        {
            Page = page > totalPages ? totalPages : page;
            Results = results;
            TotalPages = totalPages;
            TotalResults = totalResults;
        }
    }
}
