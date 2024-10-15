// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Db;

public interface ITunableDb : IDb
{
    public void Tune(TuneType type);

    enum TuneType
    {
        [Description]
        Default,
        [Description]
        WriteBias,
        [Description]
        HeavyWrite,
        [Description]
        AggressiveHeavyWrite,
        [Description]
        DisableCompaction,
        [Description]
        EnableBlobFiles,
        [Description]
        HashDb
    }
}
