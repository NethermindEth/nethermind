// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db
{
    public interface IFullDb : IDb
    {
        ICollection<byte[]> Keys { get; }

        ICollection<byte[]?> Values { get; }

        int Count { get; }
    }
}
