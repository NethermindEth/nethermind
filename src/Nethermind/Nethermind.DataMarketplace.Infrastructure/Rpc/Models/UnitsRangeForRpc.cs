// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class UnitsRangeForRpc
    {
        public uint From { get; set; }
        public uint To { get; set; }

        public UnitsRangeForRpc()
        {
        }

        public UnitsRangeForRpc(UnitsRange range)
        {
            From = range.From;
            To = range.To;
        }
    }
}
