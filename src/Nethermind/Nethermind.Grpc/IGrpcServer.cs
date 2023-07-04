// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Grpc
{
    public interface IGrpcServer
    {
        Task PublishAsync<T>(T data, string client) where T : class;
    }
}
