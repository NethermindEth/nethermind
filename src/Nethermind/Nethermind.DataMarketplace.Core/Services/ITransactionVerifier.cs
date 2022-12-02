// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface ITransactionVerifier
    {
        Task<TransactionVerifierResult> VerifyAsync(NdmTransaction transaction);
    }
}
