// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;

public interface ISubnetSnapshotManager : ISnapshotManager
{
    SubnetSnapshot GetSnapshotByHash(Hash256 headerHash);
}
