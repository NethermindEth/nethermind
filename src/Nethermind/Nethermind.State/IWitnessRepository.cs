// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    /// <summary>
    /// Allows to access persisted witnesses 
    /// </summary>
    /// <remarks>
    /// Witnesses can be pruned (deleted) to decrease space that is used 
    /// </remarks>
    public interface IWitnessRepository
    {
        Keccak[]? Load(Keccak blockHash);

        void Delete(Keccak blockHash);
    }
}
