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

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public struct ExecutionEnvironment
    {
        /// <summary>
        /// Currently executing account (in DELEGATECALL this will be equal to caller).
        /// </summary>
        public Address ExecutingAccount { get; set; }

        /// <summary>
        /// Transaction originator
        /// </summary>
        public Address Originator { get; set; }

        /// <summary>
        /// Caller
        /// </summary>
        public Address Sender { get; set; }
        
        /// <summary>
        /// Bytecode source (account address).
        /// </summary>
        public Address CodeSource { get; set; }

        /// <summary>
        /// Gas price information from the transaction environment.
        /// </summary>
        public UInt256 GasPrice { get; set; }

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public byte[] InputData { get; set; }

        /// <summary>
        /// ETH value transferred in this call.
        /// </summary>
        public UInt256 TransferValue { get; set; }

        /// <summary>
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public UInt256 Value { get; set; }

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public CodeInfo CodeInfo { get; set; }

        /// <summary>
        /// Block within which the current transaction is executed.
        /// </summary>
        public BlockHeader CurrentBlock { get; set; }
        
        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public int CallDepth { get; set; }
    }
}