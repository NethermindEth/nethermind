// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Installs the <see href="https://eips.ethereum.org/EIPS/eip-7997">EIP-7997</see> deterministic
/// deployment factory at its fixed address on the fork activation block.
/// </summary>
/// <remarks>
/// The account is always read first so it is recorded in the block access list at the pre-execution
/// index: when the canonical code is already present (the mainnet case) the read alone adds an
/// access-only entry; otherwise the code is installed and a zero nonce is bumped to one, while an
/// existing nonzero nonce and the balance are preserved.
/// </remarks>
internal static class Eip7997FactoryDeployer
{
    private static readonly ValueHash256 CanonicalCodeHash = ValueKeccak.Compute(Eip7997Constants.Code);

    public static void Deploy(IWorldState state, IReleaseSpec spec)
    {
        Address factory = Eip7997Constants.FactoryAddress;
        if (state.GetCodeHash(factory) == CanonicalCodeHash)
        {
            return;
        }

        state.CreateAccountIfNotExists(factory, UInt256.Zero);
        if (state.GetNonce(factory) == 0)
        {
            state.SetNonce(factory, 1);
        }
        state.InsertCode(factory, Eip7997Constants.Code, spec);
    }
}
