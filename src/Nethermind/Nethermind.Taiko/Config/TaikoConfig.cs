// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Config;

public class TaikoConfig : ITaikoConfig
{
    public string? L1EthApiEndpoint { get; set; } = "http://host.docker.internal:32002";
}
