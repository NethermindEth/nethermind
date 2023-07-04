// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models
{
    public class BlockParameterModel
    {
        public string Type { get; set; }
        public UInt256? Number { get; set; }

        public static BlockParameterModel FromNumber(long number) => new()
        {
            Number = (UInt256?)number
        };

        public static BlockParameterModel FromNumber(in UInt256 number) => new()
        {
            Number = number
        };

        public static BlockParameterModel Earliest => new()
        {
            Type = "earliest"
        };

        public static BlockParameterModel Latest => new()
        {
            Type = "latest"
        };


        public static BlockParameterModel Pending => new()
        {
            Type = "pending"
        };

        public static BlockParameterModel Finalized => new()
        {
            Type = "finalized"
        };

        public static BlockParameterModel Safe => new()
        {
            Type = "safe"
        };
    }
}
