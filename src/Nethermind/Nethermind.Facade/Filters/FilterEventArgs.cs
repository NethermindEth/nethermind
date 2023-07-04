// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Filters
{
    public class FilterEventArgs : EventArgs
    {
        public int FilterId { get; }

        public FilterEventArgs(int filterId)
        {
            FilterId = filterId;
        }
    }
}
