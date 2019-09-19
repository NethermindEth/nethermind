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

using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Facade.Proxy.Models
{
    public class BlockParameterModel
    {
        public string Type { get; set; }
        public UInt256? Number { get; set; }

        public static BlockParameterModel FromNumber(long number) => new BlockParameterModel
        {
            Number = (UInt256?) number
        };

        public static BlockParameterModel FromNumber(UInt256 number) => new BlockParameterModel
        {
            Number = number
        };

        public static BlockParameterModel Earliest => new BlockParameterModel
        {
            Type = "earliest"
        };

        public static BlockParameterModel Latest => new BlockParameterModel
        {
            Type = "latest"
        };


        public static BlockParameterModel Pending => new BlockParameterModel
        {
            Type = "pending"
        };
    }
}