// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Temporarily raises the synchronous block-processing thread's managed and native priority.
/// </summary>
/// <remarks>
/// The scope must not cross an await. Native nice values belong to the current native thread.
/// </remarks>
internal ref struct BlockProcessingPriorityScope
{
    private const int PrioProcess = 0;
    private const int PrimaryNice = -20;
    private const int FallbackNice = -6;
    private const int NativeApiUnavailableError = int.MinValue;

    private readonly ThreadExtensions.Disposable _managedPriority;
    private readonly ILogger _logger;
    private readonly TrySetNice? _setNice;
    private readonly int _originalNice;
    private bool _disposed;

    private BlockProcessingPriorityScope(
        ThreadExtensions.Disposable managedPriority,
        ILogger logger,
        TrySetNice? setNice,
        int originalNice)
    {
        _managedPriority = managedPriority;
        _logger = logger;
        _setNice = setNice;
        _originalNice = originalNice;
    }

    internal static BlockProcessingPriorityScope Enter(ILogger logger)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new BlockProcessingPriorityScope(Thread.CurrentThread.SetHighestPriority(), logger, null, 0);
        }

        return Enter(logger, TryGetCurrentNice, TrySetCurrentNice);
    }

    internal static BlockProcessingPriorityScope Enter(ILogger logger, TryGetNice getNice, TrySetNice setNice)
    {
        if (!getNice(out int originalNice, out int error))
        {
            LogDebug(logger, "getpriority", error);
            return new BlockProcessingPriorityScope(Thread.CurrentThread.SetHighestPriority(), logger, null, 0);
        }

        ThreadExtensions.Disposable managedPriority = Thread.CurrentThread.SetHighestPriority();
        int requestedNice = PrimaryNice;
        if (!setNice(requestedNice, out error))
        {
            int primaryError = error;
            requestedNice = Math.Min(originalNice, FallbackNice);
            if (!setNice(requestedNice, out error))
            {
                LogDebug(logger, "setpriority", primaryError, error);
                return new BlockProcessingPriorityScope(managedPriority, logger, null, originalNice);
            }
        }

        return new BlockProcessingPriorityScope(
            managedPriority,
            logger,
            requestedNice != originalNice ? setNice : null,
            originalNice);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _managedPriority.Dispose();
        }
        finally
        {
            if (_setNice is not null && !_setNice(_originalNice, out int error))
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Unable to restore native block-processing priority (setpriority errno={error}).");
                }
            }
        }
    }

    private static bool TryGetCurrentNice(out int nice, out int error)
    {
        try
        {
            Marshal.SetLastPInvokeError(0);
            nice = getpriority(PrioProcess, 0);
            error = nice == -1 ? Marshal.GetLastPInvokeError() : 0;
            return nice != -1 || error == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            nice = 0;
            error = NativeApiUnavailableError;
            return false;
        }
    }

    private static bool TrySetCurrentNice(int nice, out int error)
    {
        try
        {
            int result = setpriority(PrioProcess, 0, nice);
            error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            return result == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            error = NativeApiUnavailableError;
            return false;
        }
    }

    private static void LogDebug(ILogger logger, string operation, int error)
    {
        if (logger.IsDebug)
        {
            logger.Debug($"Unable to set native block-processing priority ({operation} errno={error}).");
        }
    }

    private static void LogDebug(ILogger logger, string operation, int firstError, int secondError)
    {
        if (logger.IsDebug)
        {
            logger.Debug($"Unable to set native block-processing priority ({operation} errno={firstError}, fallback errno={secondError}).");
        }
    }

    internal delegate bool TryGetNice(out int nice, out int error);
    internal delegate bool TrySetNice(int nice, out int error);

    [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int getpriority(int which, int who);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int setpriority(int which, int who, int priority);
}
