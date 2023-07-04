// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class CallResultWithProof
    {
        public byte[] Result { get; set; }

        public AccountProof[] Accounts { get; set; }

        public byte[][] BlockHeaders { get; set; }
    }
}
