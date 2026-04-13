// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.BlockAccessLists;

public interface IIndexedChange
{
    public ushort BlockAccessIndex { get; init; }
}
