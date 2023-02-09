// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Data
{
    public class EmptyLocalDataSource<T> : ILocalDataSource<T>
    {
        public T Data { get; } = default;

        public event EventHandler Changed
        {
            add { }
            remove { }
        }
    }
}
