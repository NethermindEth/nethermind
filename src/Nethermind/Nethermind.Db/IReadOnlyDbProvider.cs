// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public interface IReadOnlyDbProvider : IDbProvider
    {
        void ClearTempChanges();
    }
}
