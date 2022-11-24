// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public enum UpdatedTransactionStatus
    {
        Ok,
        MissingTransaction,
        InvalidGasPrice,
        ResourceNotFound,
        ResourceConfirmed,
        ResourceCancelled,
        ResourceRejected,
        AlreadyIncluded
    }
}
