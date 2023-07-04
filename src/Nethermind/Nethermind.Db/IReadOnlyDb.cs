// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public interface IReadOnlyDb : IDb
    {
        void ClearTempChanges();
    }
}
