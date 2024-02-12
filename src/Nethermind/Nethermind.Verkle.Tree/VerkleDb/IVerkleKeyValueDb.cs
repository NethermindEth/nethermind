// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.Verkle.Tree.VerkleDb;

public interface IVerkleKeyValueDb
{
    public IDb LeafDb { get; }
    public IDb InternalNodeDb { get; }
}
