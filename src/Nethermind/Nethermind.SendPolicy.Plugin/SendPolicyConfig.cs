// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.SendPolicy.Plugin;

public class SendPolicyConfig : ISendPolicyConfig
{
    public bool Enabled { get; set; } = false;
    public string? RulesPath { get; set; } = null;
    public bool WarnOnly { get; set; } = false;
}
