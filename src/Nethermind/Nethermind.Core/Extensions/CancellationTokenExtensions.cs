// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public static bool CancelDisposeAndClear(ref CancellationTokenSource? cancellationTokenSource)
        {
            CancellationTokenSource? source = cancellationTokenSource;
            if (source is not null)
            {
                source = Interlocked.CompareExchange(ref cancellationTokenSource, null, source);
                if (source is not null)
                {
                    source.Cancel();
                    source.Dispose();
                    return true;
                }
            }

            return false;
        }
    }
}
