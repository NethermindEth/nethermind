// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Dns;

public abstract class EnrTreeNode
{
    public abstract string[] Links { get; }
    public abstract string[] Refs { get; }
    public abstract string[] Records { get; }
}
