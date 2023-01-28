// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Options;

namespace Nethermind.Core2.Configuration
{
    public class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class, new()
    {
        public StaticOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }

        public T Get(string name)
        {
            return CurrentValue;
        }

        public IDisposable OnChange(Action<T, string> listener)
        {
            return EmptyDisposable.Instance;
        }
    }
}
