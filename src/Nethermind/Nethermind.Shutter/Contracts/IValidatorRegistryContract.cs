// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Shutter.Config;
using Update = (byte[] Message, byte[] Signature);

namespace Nethermind.Shutter.Contracts;

public interface IValidatorRegistryContract
{
    /// <summary>
    /// Check if validator is registered.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="signature"></param>
    bool IsRegistered(in BlockHeader header, in ShutterValidatorsInfo validatorsInfo, out HashSet<ulong> unregistered);

    /// <summary>
    /// Returns the number of previous updates to the registry.
    /// </summary>
    /// <param name="blockHeader"></param>
    UInt256 GetNumUpdates(BlockHeader header);

    /// <summary>
    /// Retrieves the ith update to the registry.
    /// </summary>
    /// <param name="blockHeader"></param>
    /// <param name="i"></param>
    Update GetUpdate(BlockHeader header, in UInt256 i);
}
