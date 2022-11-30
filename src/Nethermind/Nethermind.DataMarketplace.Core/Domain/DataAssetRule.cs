// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DataAssetRule
    {
        public UInt256 Value { get; }

        public DataAssetRule(UInt256 value)
        {
            Value = value;
        }
    }
}
