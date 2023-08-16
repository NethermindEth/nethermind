// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree.VerkleDb;

public interface IVerkleMemDb
{
    public LeafStore LeafTable { get; }
    public InternalStore InternalTable { get; }
}
