/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Ethereum.Test.Base
{
    public class GeneralStateTest : IEthereumTest
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public IReleaseSpec? Fork { get; set; }
        public string? ForkName { get; set; }
        public Address? CurrentCoinbase { get; set; }
        public UInt256 CurrentDifficulty { get; set; }
        
        public UInt256? CurrentBaseFee { get; set; }
        public long CurrentGasLimit { get; set; }
        public long CurrentNumber { get; set; }
        public UInt256 CurrentTimestamp { get; set; }
        public Keccak? PreviousHash { get; set; }
        public Dictionary<Address, AccountState> Pre { get; set; }
        public Keccak? PostHash { get; set; }
        public Keccak? PostReceiptsRoot { get; set; }
        public string? LoadFailure { get; set; }
        public Transaction? Transaction { get; set; }
        
        public override string ToString()
        {
            return $"{Path.GetFileName(Category)}.{Name}_{ForkName}";
        }
    }
}
