/*
 * Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm
{
    public class ExecutionEnvironment
    {
        public Address ExecutingAccount { get; set; }

        public Address Originator { get; set; }

        public Address Sender { get; set; }
        
        public Address CodeSource { get; set; }

        public UInt256 GasPrice { get; set; }

        public byte[] InputData { get; set; }

        public UInt256 TransferValue { get; set; }

        public UInt256 Value { get; set; }

        public CodeInfo CodeInfo { get; set; }

        public BlockHeader CurrentBlock { get; set; }

        public int CallDepth { get; set; }
    }
}