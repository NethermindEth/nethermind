// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Core
{
    public class DisposableStack : Stack<IAsyncDisposable>
    {
        public new void Push(IAsyncDisposable item) => base.Push(item);

        public void Push(IDisposable item) => Push(new AsyncDisposableWrapper(item));

        private class AsyncDisposableWrapper : IAsyncDisposable
        {
            private readonly IDisposable _item;

            public AsyncDisposableWrapper(IDisposable item)
            {
                _item = item;
            }

            public ValueTask DisposeAsync()
            {
                _item?.Dispose();
                return default;
            }

            public override string? ToString()
            {
                return _item?.ToString() ?? base.ToString();
            }
        }
    }
}
