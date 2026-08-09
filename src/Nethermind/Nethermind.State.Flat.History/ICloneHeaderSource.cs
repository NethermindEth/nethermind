// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

public interface ICloneHeaderSource
{
    ValueHash256? TryGetStateRoot(ulong block);
}
