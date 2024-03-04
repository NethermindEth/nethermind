// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Api;

public interface IStateConfig : IConfig
{
    [ConfigItem(Description = "How much state must be kept available from the head. Determine how old of a block can eth_call function. Also determine the reorg depth", DefaultValue = "64")]
    int KeepLastNState { get; set; }
}
