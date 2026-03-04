// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

public interface IPreBlockCaches
{
    PreBlockCaches Caches { get; }
    bool IsWarmWorldState { get; }
}
