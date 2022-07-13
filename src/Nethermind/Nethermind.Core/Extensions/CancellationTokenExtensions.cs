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
//

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Extensions
{
    public static class CancellationTokenExtensions
    {
        /// <summary>
        /// Converts <see cref="CancellationToken"/> to a awaitable <see cref="Task"/> when token is cancelled.
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public static Task AsTask(this CancellationToken token)
        {
            TaskCompletionSource taskCompletionSource = new();
            token.Register(() => taskCompletionSource.TrySetCanceled(), false);
            return taskCompletionSource.Task;
        }

        /// <summary>
        /// <see cref="CancellationTokenSource.Cancel()"/>s and <see cref="CancellationTokenSource.Dispose"/>s the <see cref="CancellationTokenSource"/>
        /// and also sets it to null so multiple calls to this method are safe and <see cref="CancellationTokenSource.Cancel()"/> will be called only once.
        /// </summary>
        /// <param name="cancellationTokenSource"></param>
        /// <remarks>
        /// This method is thread-sage and uses <see cref="Interlocked.CompareExchange{T}(ref T,T,T)"/> to safely manage reference.
        /// </remarks>
        public static void CancelDisposeAndClear(ref CancellationTokenSource? cancellationTokenSource)
        {
            CancellationTokenSource? source = cancellationTokenSource;
            if (source is not null)
            {
                source = Interlocked.CompareExchange(ref cancellationTokenSource, null, source);
                if (source is not null)
                {
                    source.Cancel();
                    source.Dispose();
                }
            }
        }
    }
}
