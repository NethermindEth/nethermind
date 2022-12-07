// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core2
{
    public class EmptyDisposable : IDisposable
    {
        private EmptyDisposable()
        {
        }

        public static IDisposable Instance { get; } = new EmptyDisposable();

        public void Dispose()
        {
        }
    }
}
