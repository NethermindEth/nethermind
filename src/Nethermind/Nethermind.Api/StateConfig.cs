// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Api;

public class StateConfig : IStateConfig
{
    public int KeepLastNState { get; set; } = 64;
}
