// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.ContractSearch.Plugin;

public class ContractSearchConfig : IContractSearchConfig
{
    public bool Enabled { get; set; }
    public string? File { get; set; }
}
