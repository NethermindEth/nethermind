// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public interface ITunableDb: IDb
{
    public void Tune(TuneType type);

    enum TuneType
    {
        Default,
        WriteBias,
        HeavyWrite,
        AggressiveHeavyWrite,
        VeryAggressiveHeavyWrite,
        DisableCompaction,
    }
}
