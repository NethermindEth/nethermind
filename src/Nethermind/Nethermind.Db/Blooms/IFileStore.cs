// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Blooms
{
    public interface IFileStore : IDisposable
    {
        void Write(ulong index, ReadOnlySpan<byte> element);

        int Read(ulong index, Span<byte> element);

        IFileReader CreateFileReader();
    }
}
