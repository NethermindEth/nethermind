//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Evm.Tracing.ParityStyle
{
//    "mem": {
//"data": "0x60606040526000357c0100000000000000000000000000000000000000000000000000000000900463ffffffff168063230925601461003b575bfe5b341561004357fe5b61008360048080356000191690602001909190803560ff1690602001909190803560001916906020019091908035600019169060200190919050506100c5565b604051808273ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff16815260200191505060405180910390f35b6000600185858585604051806000526020016040526000604051602001526040518085600019166000191681526020018460ff1660ff1681526020018360001916600019168152602001826000191660001916815260200194505050505060206040516020810390808403906000866161da5a03f1151561014257fe5b50506020604051035190505b9493505050505600a165627a7a7230582054abc8e7b2d8ea0972823aa9f0df23ecb80ca0b58be9f31b7348d411aaf585be0029",
//"off": 0
//},
    public class ParityMemoryChangeTrace
    {
        public long Offset { get; set; }
        public byte[] Data { get; set; }
    }
}
