// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.ByPathState;
public class ByPathStateConfig : IByPathStateConfig
{
    public bool Enabled { get; set; } = false;
    public int InMemHistoryBlocks { get; set; } = 128;
    public int PersistenceInterval { get; set; } = 64;
}
