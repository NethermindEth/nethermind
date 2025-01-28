// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Core
{
    public class DisposableStack(ILogManager logManager) : Stack<IAsyncDisposable>, IAsyncDisposable
    {
        private readonly ILogger _logger = logManager.GetClassLogger<DisposableStack>();

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

        public async ValueTask DisposeAsync()
        {
            while (Count != 0)
            {
                IAsyncDisposable disposable = Pop();
                await Stop(async () => await disposable.DisposeAsync(), $"Disposing {disposable}");
            }
        }

        private Task Stop(Func<Task?> stopAction, string description)
        {
            try
            {
                if (_logger.IsInfo) _logger.Info(description);
                return stopAction() ?? Task.CompletedTask;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{description} shutdown error.", e);
                return Task.CompletedTask;
            }
        }
    }
}
