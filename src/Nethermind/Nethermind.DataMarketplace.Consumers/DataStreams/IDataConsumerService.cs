// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.DataStreams
{
    public interface IDataConsumerService
    {
        Task SetUnitsAsync(Keccak depositId, uint consumedUnitsFromProvider);
        Task SetDataAvailabilityAsync(Keccak depositId, DataAvailability dataAvailability);
        Task HandleInvalidDataAsync(Keccak depositId, InvalidDataReason reason);
        Task HandleGraceUnitsExceededAsync(Keccak depositId, uint consumedUnitsFromProvider, uint graceUnits);
    }
}
