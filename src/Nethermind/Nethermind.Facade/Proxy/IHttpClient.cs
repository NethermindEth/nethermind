// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Facade.Proxy
{
    public interface IHttpClient
    {
        Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default);
        Task<T> PostJsonAsync<T>(string endpoint, object? payload = null, CancellationToken cancellationToken = default);
    }
}
