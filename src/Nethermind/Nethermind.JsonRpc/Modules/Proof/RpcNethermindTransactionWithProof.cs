// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class RpcNethermindTransactionWithProof
    {
        public TransactionForRpc Transaction { get; set; }

        public byte[][] TxProof { get; set; }

        public byte[] BlockHeader { get; set; }
    }
}
