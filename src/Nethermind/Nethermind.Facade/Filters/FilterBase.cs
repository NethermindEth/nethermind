// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Filters
{
    public abstract class FilterBase
    {
        public int Id { get; }

        protected FilterBase(int id)
        {
            Id = id;
        }
    }
}
