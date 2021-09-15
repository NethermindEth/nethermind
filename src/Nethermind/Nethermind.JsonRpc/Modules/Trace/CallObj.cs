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
// 

using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class CallObj
    {
        public readonly TransactionForRpc _message;
        public readonly string[] _traceTypes;
        public readonly BlockParameter _numberOrTag;

        public CallObj(TransactionForRpc message, string[] traceTypes, BlockParameter numberOrTag)
        {
            _message = message;
            _traceTypes = traceTypes;
            _numberOrTag = numberOrTag;
        }
        
    }
}
