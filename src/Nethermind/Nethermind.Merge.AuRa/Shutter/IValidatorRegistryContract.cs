// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa.Shutter;

public interface IValidatorRegistryContract
{
    /// <summary>
    /// Removes a validator from the validator registry.
    /// </summary>
    /// <param name="blockHeader"></param>
    ValueTask<AcceptTxResult?> Deregister(BlockHeader blockHeader);

    /// <summary>
    /// Adds a validator to the validator registry.
    /// </summary>
    /// <param name="blockHeader"></param>
    ValueTask<AcceptTxResult?> Register(BlockHeader blockHeader);
}
