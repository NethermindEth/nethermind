// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Taiko;

public interface ITaikoConfig : IConfig
{
    bool Enabled { get; set; }
}
