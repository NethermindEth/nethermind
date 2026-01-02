// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Blooms
{
    public interface IFileReader : IDisposable
    {
        int Read(long index, Span<byte> element);
    }
}
