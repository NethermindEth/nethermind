// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Shutter;

public interface IValidatorRegistryContract
{
    /// <summary>
    /// Removes a validator from the validator registry.
    /// </summary>
    /// <param name="blockHeader"></param>
    /// <param name="message"></param>
    /// <param name="signature"></param>
    void Deregister(BlockHeader blockHeader, byte[] message, byte[] signature);

    /// <summary>
    /// Adds a validator to the validator registry.
    /// </summary>
    /// <param name="blockHeader"></param>
    /// <param name="message"></param>
    /// <param name="signature"></param>
    void Register(BlockHeader blockHeader, byte[] message, byte[] signature);
}
