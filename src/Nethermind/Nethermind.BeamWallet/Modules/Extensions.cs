//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.Globalization;
using System.Threading.Tasks;
using Nethermind.Facade.Proxy;
using Nethermind.Int256;

namespace Nethermind.BeamWallet.Modules
{
    internal static class Extensions
    {
        const long Number = 1000000000000000000;

        public static UInt256 EthToWei(this string result)
        {
            var resultDecimal = decimal.Parse(result);
            var resultRound = decimal.Round(resultDecimal * Number, 0);
            return UInt256.Parse(resultRound.ToString(CultureInfo.InvariantCulture));
        }

        public static decimal WeiToEth(this long result) => ((decimal)result).WeiToEth();

        public static decimal WeiToEth(this decimal result) => result / Number;

        public static async Task<T> TryExecuteAsync<T>(Func<Task<RpcResult<T>>> request,
            Func<RpcResult<T>, bool> validator = null, TimeSpan? delay = null)
        {
            var interval = delay ?? TimeSpan.FromSeconds(2);
            RpcResult<T> result;
            do
            {
                result = await request();
                if (result.IsValid)
                {
                    return result.Result;
                }

                await Task.Delay(interval);
            } while (!result.IsValid || (validator?.Invoke(result) ?? true));

            return default;
        }
        
        public static async Task<T> TryExecuteAsync<T>(Func<Task<T>> request,
            Func<T, bool> validator = null, TimeSpan? delay = null)
        {
            var interval = delay ?? TimeSpan.FromSeconds(2);
            T result;
            while (true)
            {
                result = await request();
                if (result is {} && (validator?.Invoke(result) ?? true))
                {
                    return result;
                }

                await Task.Delay(interval);
            }
        }
    }
}
