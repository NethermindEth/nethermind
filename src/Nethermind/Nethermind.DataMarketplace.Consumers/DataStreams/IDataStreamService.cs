// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Consumers.DataStreams
{
    public interface IDataStreamService
    {
        Task<Keccak?> EnableDataStreamAsync(Keccak depositId, string client, string?[] args);
        Task<Keccak?> DisableDataStreamAsync(Keccak depositId, string client);
        Task<Keccak?> DisableDataStreamsAsync(Keccak depositId);
        Task SetEnabledDataStreamAsync(Keccak depositId, string client, string?[] args);
        Task SetDisabledDataStreamAsync(Keccak depositId, string client);
    }
}
