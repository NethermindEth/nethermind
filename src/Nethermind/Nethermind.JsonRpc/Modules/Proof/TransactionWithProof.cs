// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class TransactionWithProof
    {
        public TransactionForRpc Transaction { get; set; }

        public byte[][] TxProof { get; set; }

        public byte[] BlockHeader { get; set; }
    }
}
