// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System.Collections.Generic;

namespace Nethermind.Xdc.RPC;

public class PublicApiSnapshot
{
    /// <summary>
    /// Block number where the snapshot was created
    /// </summary>
    public ulong Number { get; set; }

    /// <summary>
    /// Block hash where the snapshot was created
    /// </summary>
    public Hash256 Hash { get; set; } = null!;

    /// <summary>
    /// Set of authorized signers at this moment
    /// </summary>
    public HashSet<Address> Signers { get; set; } = null!;

}
