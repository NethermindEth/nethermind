// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptsMigration
    {
        Task<bool> Run(long blockNumber);
    }
}
