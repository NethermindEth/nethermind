// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public interface ITunableDb
{
    public void Tune(TuneType type);

    enum TuneType
    {
        Default,
        WriteBias,
        HeavyWrite,
        AggressiveHeavyWrite,
        DisableCompaction,
        EnableBlobFiles,
        HashDb
    }
}

public class NoopTunableDb : ITunableDb
{
    public void Tune(ITunableDb.TuneType type)
    {
    }
}
