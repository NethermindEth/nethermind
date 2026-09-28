// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ReceiptWithProof : IDisposable
    {
        public ReceiptForRpc Receipt { get; set; }

        public byte[][] TxProof { get; set; }

        public byte[][] ReceiptProof { get; set; }

        public byte[] BlockHeader { get; set; }

        public void Dispose() => Receipt?.Dispose();
    }
}
