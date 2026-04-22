// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Filters
{
    public class FilterEventArgs(int filterId) : EventArgs
    {
        public int FilterId { get; } = filterId;
    }
}
