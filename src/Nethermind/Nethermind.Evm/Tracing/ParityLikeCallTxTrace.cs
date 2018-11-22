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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public class ParityTraceAction
    {
        public string CallType { get; set; }
        public Address From { get; set; }
        public long Gas { get; set; }
        public byte[] Input { get; set; }
        public Address To { get; set; }
        public UInt256 Value { get; set; }
    }
    
    public class ParityTraceResult
    {
        public long GasUsed { get; set; }
        public byte[] Output { get; set; }   
    }
    
    public class ParityLikeCallTxTrace
    {
        public ParityTraceAction Action { get; set; }

        public ParityTraceResult Result { get; set; }
        
        public Keccak BlockHash { get; set; }
        
        public UInt256 BlockNumber { get; set; }

        public ParityTraceAction[] Subtraces { get; set; }
        
        public string TraceAddress { get; set; }
        
        public Keccak TransactionHash { get; set; }
        
        public int TransactionPosition { get; set; }
        
        public string Type { get; set; }
        
        //                "action": {
//                    "callType": "call",
//                    "from": "0x8c5643148fa92a0c8d5da9dc0862ac11ef1da47c",
//                    "gas": "0x35eb8",
//                    "input": "0x278b8c0e0000000000000000000000008f3470a7388c05ee4e7af3d01d8c722b0ff523740000000000000000000000000000000000000000000000000de0b6b3a7640000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002308a0d5f892a000000000000000000000000000000000000000000000000000000000000671b3200000000000000000000000000000000000000000000000000000000eda62861000000000000000000000000000000000000000000000000000000000000001c5c261d1e1319a9a1a43cf41586d250f419799c34a8457b1b6a66f14db3bc4e5161145e4acc6fbcd4eb8df2467c2cebaea218b1aee0facfb128c455604cdd7166",
//                    "to": "0x8d12a197cb00d4747a1fe03395095ce2a5cc6819",
//                    "value": "0x0"
//                },
//                "blockHash": "0x96b5d72b8ecae3f882a3dee6dc85d5c7e0106b752ccc8f875047fad3368d515a",
//                "blockNumber": 6749171,
//                "result": {
//                    "gasUsed": "0x3b2a",
//                    "output": "0x"
//                },
//                "subtraces": 0,
//                "traceAddress": [],
//                "transactionHash": "0xd392fbff39eb795eea46843c690a5d49854d4703364a87fe828c6cf46ff6f760",
//                "transactionPosition": 11,
//                "type": "call"
//            }
    }
}