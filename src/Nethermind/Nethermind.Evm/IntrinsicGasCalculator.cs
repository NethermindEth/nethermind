//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class IntrinsicGasCalculator
    {
        public static long Calculate(Transaction transaction, IReleaseSpec releaseSpec)
        {
            long result = GasCostOf.Transaction;
            long txDataNonZeroGasCost = releaseSpec.IsEip2028Enabled ? GasCostOf.TxDataNonZeroEip2028 : GasCostOf.TxDataNonZero;

            if (transaction.Data != null)
            {
                for (int i = 0; i < transaction.Data.Length; i++)
                {
                    result += transaction.Data[i] == 0 ? GasCostOf.TxDataZero : txDataNonZeroGasCost;
                }
            }

            if (transaction.IsContractCreation && releaseSpec.IsEip2Enabled)
            {
                result += GasCostOf.TxCreate;
            }

            if (transaction.AccessList != null)
            {
                if (releaseSpec.UseTxAccessLists)
                {
                    result += transaction.AccessList.Addresses.Count * GasCostOf.AccessAccountListEntry;
                    result += transaction.AccessList.StorageCells.Count * GasCostOf.AccessStorageListEntry;       
                }
                else
                {
                    throw new InvalidDataException(
                        $"Transaction with an access list received within the context of {releaseSpec.Name}");
                }
            }

            return result;
        }
    }
}
