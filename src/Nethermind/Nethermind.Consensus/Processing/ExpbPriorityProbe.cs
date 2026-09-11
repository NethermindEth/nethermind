// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

internal enum ExpbPriorityMode
{
    Off,
    Observe,
    Nice,
}

/// <summary>
/// Optionally records and adjusts the Linux scheduling state of the synchronous block-processing thread.
/// </summary>
/// <remarks>
/// This probe is deliberately local to <see cref="BlockchainProcessor"/>. It must not be used around an
/// asynchronous operation because Linux nice values belong to native threads, while an async continuation can
/// resume on another thread.
/// </remarks>
internal sealed class ExpbPriorityProbe
{
    internal const string EnvironmentVariable = "NETHERMIND_EXPB_PRIORITY_MODE";
    private const int RequestedNice = -5;

    private readonly ExpbPriorityMode _mode;
    private readonly IExpbPriorityNative _native;
    private readonly ILogger _logger;
    private readonly HashSet<int> _loggedThreadIds = [];
    private readonly HashSet<int> _loggedFailureThreadIds = [];
    private const int MaxSuccessfulThreadRecords = 64;
    private bool _loggedUnknownThread;
    private bool _loggedUnknownFailure;

    internal ExpbPriorityProbe(ExpbPriorityMode mode, IExpbPriorityNative native, ILogger logger)
    {
        _mode = mode;
        _native = native;
        _logger = logger;
    }

    internal static ExpbPriorityProbe FromEnvironment(ILogger logger)
    {
        string? rawMode = Environment.GetEnvironmentVariable(EnvironmentVariable);
        ExpbPriorityMode mode = ParseMode(rawMode);

        if (mode is not ExpbPriorityMode.Off && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                $"{EnvironmentVariable}={rawMode} requires Linux; unset the variable or set it to off on this platform.");
        }

        return new ExpbPriorityProbe(mode, LinuxExpbPriorityNative.Instance, logger);
    }

    internal static ExpbPriorityMode ParseMode(string? rawMode)
    {
        if (string.IsNullOrWhiteSpace(rawMode) || rawMode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return ExpbPriorityMode.Off;
        }

        if (rawMode.Equals("observe", StringComparison.OrdinalIgnoreCase))
        {
            return ExpbPriorityMode.Observe;
        }

        if (rawMode.Equals("nice", StringComparison.OrdinalIgnoreCase))
        {
            return ExpbPriorityMode.Nice;
        }

        throw new InvalidOperationException(
            $"Invalid {EnvironmentVariable} value '{rawMode}'. Expected unset, off, observe, or nice.");
    }

    internal Scope Enter()
    {
        if (_mode is ExpbPriorityMode.Off)
        {
            return default;
        }

        ProbeState state = new(_mode);
        if (!_native.TryGetThreadId(out state.ThreadId, out int error))
        {
            state.Failure = $"gettid errno={error}";
            return new Scope(this, state);
        }

        if (!_native.TryGetSchedulingPolicy(state.ThreadId, out state.PolicyBefore, out error))
        {
            state.Failure = $"sched_getscheduler_before errno={error}";
            return new Scope(this, state);
        }

        if (!_native.TryGetNice(state.ThreadId, out state.NiceBefore, out error))
        {
            state.Failure = $"getpriority_before errno={error}";
            return new Scope(this, state);
        }

        if (_mode is ExpbPriorityMode.Nice)
        {
            state.SetAttempted = true;
            if (!_native.TrySetNice(state.ThreadId, RequestedNice, out error))
            {
                state.Failure = $"setpriority_apply errno={error}";
            }
            else if (!_native.TryGetNice(state.ThreadId, out state.NiceDuring, out error))
            {
                state.Failure = $"getpriority_during errno={error}";
            }
            else if (state.NiceDuring != RequestedNice)
            {
                state.Failure = $"setpriority_apply_readback expected={RequestedNice} actual={state.NiceDuring}";
            }
        }

        return new Scope(this, state);
    }

    private void Log(ProbeState state)
    {
        if (state.ThreadId > 0)
        {
            HashSet<int> loggedThreadIds = state.Failure is null ? _loggedThreadIds : _loggedFailureThreadIds;
            int maxRecords = MaxSuccessfulThreadRecords;
            if (loggedThreadIds.Count >= maxRecords || !loggedThreadIds.Add(state.ThreadId))
            {
                return;
            }
        }
        else if ((state.Failure is not null && _loggedUnknownFailure)
            || (state.Failure is null && _loggedUnknownThread))
        {
            return;
        }
        else
        {
            if (state.Failure is null)
            {
                _loggedUnknownThread = true;
            }
            else
            {
                _loggedUnknownFailure = true;
            }
        }

        string policyBefore = FormatPolicy(state.PolicyBefore);
        string policyDuring = FormatPolicy(state.PolicyDuring);
        string policyAfter = FormatPolicy(state.PolicyAfter);
        string niceBefore = FormatValue(state.NiceBefore);
        string niceDuring = FormatValue(state.NiceDuring);
        string niceAfter = FormatValue(state.NiceAfter);
        string failure = state.Failure is null ? string.Empty : $" failure={state.Failure}";
        _logger.Info(
            $"EXPB_PRIORITY mode={_mode.ToString().ToLowerInvariant()} tid={state.ThreadId} "
            + $"policy_before={policyBefore} nice_before={niceBefore} managed_during={state.ManagedDuring ?? "unknown"} "
            + $"policy_during={policyDuring} nice_during={niceDuring} "
            + $"policy_after={policyAfter} nice_after={niceAfter} success={(state.Failure is null ? "true" : "false")}{failure}");
    }

    private static string FormatValue(int value) => value == int.MinValue ? "unknown" : value.ToString();

    private static string FormatPolicy(int value)
        => value switch
        {
            int.MinValue => "unknown",
            0 => "SCHED_OTHER",
            1 => "SCHED_FIFO",
            2 => "SCHED_RR",
            3 => "SCHED_BATCH",
            5 => "SCHED_IDLE",
            6 => "SCHED_DEADLINE",
            _ => value.ToString(),
        };

    internal struct Scope : IDisposable
    {
        private readonly ExpbPriorityProbe? _owner;
        private ProbeState _state;

        internal Scope(ExpbPriorityProbe owner, ProbeState state)
        {
            _owner = owner;
            _state = state;
        }

        internal void CaptureDuring()
        {
            if (_owner is null || _state.ThreadId <= 0 || _state.Failure is not null)
            {
                return;
            }

            ThreadPriority managedPriority = Thread.CurrentThread.Priority;
            _state.ManagedDuring = managedPriority.ToString();
            if (managedPriority is not ThreadPriority.Highest)
            {
                _state.Failure = $"managed_priority_during expected=Highest actual={_state.ManagedDuring}";
                return;
            }

            if (!_owner._native.TryGetSchedulingPolicy(_state.ThreadId, out _state.PolicyDuring, out int error))
            {
                _state.Failure = $"sched_getscheduler_during errno={error}";
                return;
            }

            if (!_owner._native.TryGetNice(_state.ThreadId, out _state.NiceDuring, out error))
            {
                _state.Failure = $"getpriority_during errno={error}";
                return;
            }

            int expectedNice = _state.Mode is ExpbPriorityMode.Nice ? RequestedNice : _state.NiceBefore;
            if (_state.NiceDuring != expectedNice)
            {
                _state.Failure = $"setpriority_during_readback expected={expectedNice} actual={_state.NiceDuring}";
            }
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            if (_state.ThreadId > 0)
            {
                if (!_owner._native.TryGetThreadId(out int currentThreadId, out int error))
                {
                    _state.Failure ??= $"gettid_after errno={error}";
                }
                else if (currentThreadId != _state.ThreadId)
                {
                    _state.Failure ??= $"native_thread_changed before={_state.ThreadId} after={currentThreadId}";
                }
                else if (_state.Mode is ExpbPriorityMode.Nice && _state.SetAttempted)
                {
                    if (!_owner._native.TrySetNice(_state.ThreadId, _state.NiceBefore, out error))
                    {
                        _state.Failure ??= $"setpriority_restore errno={error}";
                    }
                }

                if (!_owner._native.TryGetSchedulingPolicy(_state.ThreadId, out _state.PolicyAfter, out error))
                {
                    _state.Failure ??= $"sched_getscheduler_after errno={error}";
                }

                if (!_owner._native.TryGetNice(_state.ThreadId, out _state.NiceAfter, out error))
                {
                    _state.Failure ??= $"getpriority_after errno={error}";
                }

                if (_state.Mode is ExpbPriorityMode.Nice
                    && _state.NiceAfter != _state.NiceBefore)
                {
                    _state.Failure ??= $"setpriority_restore_readback expected={_state.NiceBefore} actual={_state.NiceAfter}";
                }

                if (_state.PolicyDuring != int.MinValue && _state.PolicyAfter != _state.PolicyDuring)
                {
                    _state.Failure ??= $"scheduler_policy_changed during={_state.PolicyDuring} after={_state.PolicyAfter}";
                }

                if (_state.PolicyBefore != int.MinValue && _state.PolicyDuring != int.MinValue
                    && _state.PolicyDuring != _state.PolicyBefore)
                {
                    _state.Failure ??= $"scheduler_policy_changed before={_state.PolicyBefore} during={_state.PolicyDuring}";
                }

                if (_state.Mode is ExpbPriorityMode.Observe
                    && _state.NiceAfter != int.MinValue && _state.NiceAfter != _state.NiceBefore)
                {
                    _state.Failure ??= $"nice_changed before={_state.NiceBefore} after={_state.NiceAfter}";
                }
            }

            _state.Failure ??= _state.ManagedDuring is null ? "managed_priority_during not captured" : null;

            _owner.Log(_state);
        }
    }

    internal sealed class ProbeState
    {
        internal ProbeState(ExpbPriorityMode mode)
        {
            Mode = mode;
            ThreadId = -1;
            PolicyBefore = int.MinValue;
            PolicyDuring = int.MinValue;
            PolicyAfter = int.MinValue;
            NiceBefore = int.MinValue;
            NiceDuring = int.MinValue;
            NiceAfter = int.MinValue;
        }

        internal ExpbPriorityMode Mode { get; }
        internal int ThreadId;
        internal int PolicyBefore;
        internal int PolicyDuring;
        internal int PolicyAfter;
        internal int NiceBefore;
        internal int NiceDuring;
        internal int NiceAfter;
        internal string? ManagedDuring;
        internal bool SetAttempted;
        internal string? Failure;
    }
}

internal interface IExpbPriorityNative
{
    bool TryGetThreadId(out int threadId, out int error);
    bool TryGetSchedulingPolicy(int threadId, out int policy, out int error);
    bool TryGetNice(int threadId, out int nice, out int error);
    bool TrySetNice(int threadId, int nice, out int error);
}

internal sealed class LinuxExpbPriorityNative : IExpbPriorityNative
{
    internal static readonly LinuxExpbPriorityNative Instance = new();
    private const int PrioProcess = 0;

    private LinuxExpbPriorityNative() { }

    public bool TryGetThreadId(out int threadId, out int error)
    {
        threadId = GetThreadId();
        error = threadId > 0 ? 0 : Marshal.GetLastPInvokeError();
        return threadId > 0;
    }

    public bool TryGetSchedulingPolicy(int threadId, out int policy, out int error)
    {
        policy = sched_getscheduler(threadId);
        error = policy >= 0 ? 0 : Marshal.GetLastPInvokeError();
        return policy >= 0;
    }

    public bool TryGetNice(int threadId, out int nice, out int error)
    {
        Marshal.SetLastPInvokeError(0);
        nice = getpriority(PrioProcess, threadId);
        error = nice == -1 ? Marshal.GetLastPInvokeError() : 0;
        return nice != -1 || error == 0;
    }

    public bool TrySetNice(int threadId, int nice, out int error)
    {
        int result = setpriority(PrioProcess, threadId, nice);
        error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        return result == 0;
    }

    [DllImport("libc", EntryPoint = "gettid", SetLastError = true)]
    private static extern int GetThreadId();

    [DllImport("libc", EntryPoint = "sched_getscheduler", SetLastError = true)]
    private static extern int sched_getscheduler(int threadId);

    [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int getpriority(int which, int who);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int setpriority(int which, int who, int priority);
}
