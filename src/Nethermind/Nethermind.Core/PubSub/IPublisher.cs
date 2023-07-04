// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Core.PubSub
{
    public interface IPublisher : IDisposable
    {
        Task PublishAsync<T>(T data) where T : class;
    }
}
