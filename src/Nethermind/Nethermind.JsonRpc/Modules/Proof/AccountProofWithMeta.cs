// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class AccountProofWithMeta
    {
        public AccountProof Proof { get; set; } = null!;
        public ProofMeta Meta { get; set; } = null!;
    }
}
