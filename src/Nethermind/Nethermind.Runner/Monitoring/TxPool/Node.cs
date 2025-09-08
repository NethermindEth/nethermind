// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Runner.Monitoring.TransactionPool;

internal class Node(string name, bool inclusion = false)
{
    public string Name { get; set; } = name;
    public bool Inclusion { get; set; } = inclusion;
}
