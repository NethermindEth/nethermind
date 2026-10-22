// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// Response payload of <c>proof_getProofWithMeta</c> — the EIP-1186 account proof plus
    /// per-call diagnostics from <see cref="ProofMeta"/>.
    /// </summary>
    public class AccountProofWithMeta
    {
        /// <summary>EIP-1186 account proof, identical in shape to <c>eth_getProof</c>'s result.</summary>
        public AccountProof Proof { get; set; } = null!;

        /// <summary>Per-call diagnostics captured during proof construction.</summary>
        public ProofMeta Meta { get; set; } = null!;
    }
}
