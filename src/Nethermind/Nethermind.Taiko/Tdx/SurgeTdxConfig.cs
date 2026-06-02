// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Tdx;

public class SurgeTdxConfig : ISurgeTdxConfig
{
    public string SocketPath { get; set; } = "/var/tdxs.sock";
    public string ConfigPath { get; set; } = "~/.config/nethermind/tdx";
}

