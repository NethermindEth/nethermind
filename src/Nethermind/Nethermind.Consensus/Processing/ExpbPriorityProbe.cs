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
    Boost,
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
    internal const int NativeApiUnavailableError = int.MinValue;
    private const int RequestedNice = -5;
    private const int BoostPrimaryNice = -20;
    private const int BoostFallbackNice = -6;

    private readonly ExpbPriorityMode _mode;
    private readonly IExpbPriorityNative _native;
    private readonly ILogger _logger;
    private readonly HashSet<int> _loggedThreadIds = [];
    private readonly HashSet<int> _loggedFailureThreadIds = [];
    private const int MaxSuccessfulThreadRecords = 64;
    private int _disabled;
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
        return FromEnvironment(rawMode, OperatingSystem.IsLinux(), logger);
    }

    internal static ExpbPriorityProbe FromEnvironment(string? rawMode, bool isLinux, ILogger logger)
    {
        ExpbPriorityMode mode;
        try
        {
            mode = ParseMode(rawMode, isLinux);
        }
        catch (InvalidOperationException exception)
        {
            mode = ExpbPriorityMode.Off;
            if (logger.IsWarn)
            {
                logger.Warn($"{exception.Message} Priority probe is disabled.");
            }
        }

        if (mode is not ExpbPriorityMode.Off && !isLinux)
        {
            mode = ExpbPriorityMode.Off;
            if (logger.IsWarn)
            {
                logger.Warn($"{EnvironmentVariable}={rawMode} requires Linux; priority probe is disabled.");
            }
        }

        return new ExpbPriorityProbe(mode, LinuxExpbPriorityNative.Instance, logger);
    }

    internal static ExpbPriorityMode ParseMode(string? rawMode)
        => ParseMode(rawMode, OperatingSystem.IsLinux());

    internal static ExpbPriorityMode ParseMode(string? rawMode, bool isLinux)
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

        if (rawMode.Equals("boost", StringComparison.OrdinalIgnoreCase))
        {
            return ExpbPriorityMode.Boost;
        }

        throw new InvalidOperationException(
            $"Invalid {EnvironmentVariable} value '{rawMode}'. Expected unset, off, observe, nice, or boost.");
    }

    internal Scope Enter()
    {
        if (_mode is ExpbPriorityMode.Off || Volatile.Read(ref _disabled) != 0)
        {
            return default;
        }

        ProbeState state = new(_mode);
        int error;
        try
        {
            if (!_native.TryGetThreadId(out state.ThreadId, out error))
            {
                state.Failure = FormatError("gettid", error);
                return new Scope(this, state);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            state.Failure = "gettid unavailable";
            return new Scope(this, state);
        }

        if (!_native.TryGetSchedulingPolicy(state.ThreadId, out state.PolicyBefore, out error))
        {
            state.Failure = FormatError("sched_getscheduler_before", error);
            return new Scope(this, state);
        }

        if (!_native.TryGetNice(state.ThreadId, out state.NiceBefore, out error))
        {
            state.Failure = FormatError("getpriority_before", error);
            return new Scope(this, state);
        }

        if (_mode is ExpbPriorityMode.Nice or ExpbPriorityMode.Boost)
        {
            int requestedNice = _mode is ExpbPriorityMode.Boost ? BoostPrimaryNice : RequestedNice;
            if (_native.TrySetNice(state.ThreadId, requestedNice, out error))
            {
                state.AppliedNice = true;
                state.AppliedNiceValue = requestedNice;
                if (!_native.TryGetNice(state.ThreadId, out state.NiceDuring, out error))
                {
                    state.Failure = FormatError("getpriority_during", error);
                }
                else if (state.NiceDuring != requestedNice)
                {
                    state.Failure = $"setpriority_apply_readback expected={requestedNice} actual={state.NiceDuring}";
                }
            }
            else if (_mode is ExpbPriorityMode.Nice)
            {
                state.Failure = FormatError("setpriority_apply", error);
                if (IsPermissionDenied(error))
                {
                    Volatile.Write(ref _disabled, 1);
                }
            }
            else
            {
                int primaryError = error;
                int fallbackNice = Math.Min(state.NiceBefore, BoostFallbackNice);
                if (_native.TrySetNice(state.ThreadId, fallbackNice, out error))
                {
                    state.AppliedNice = true;
                    state.AppliedNiceValue = fallbackNice;
                    if (!_native.TryGetNice(state.ThreadId, out state.NiceDuring, out error))
                    {
                        state.Failure = FormatError("getpriority_fallback", error);
                    }
                    else if (state.NiceDuring != fallbackNice)
                    {
                        state.Failure = $"setpriority_fallback_readback expected={fallbackNice} actual={state.NiceDuring}";
                    }
                }
                else
                {
                    state.Failure = $"{FormatError("setpriority_primary", primaryError)}; {FormatError("setpriority_fallback", error)}";
                    if (IsPermissionDenied(primaryError) && IsPermissionDenied(error))
                    {
                        Volatile.Write(ref _disabled, 1);
                    }
                }
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

    internal ref struct Scope : IDisposable
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
            if (_owner is null || _state.ThreadId <= 0)
            {
                return;
            }

            ThreadPriority managedPriority = Thread.CurrentThread.Priority;
            _state.ManagedDuring = managedPriority.ToString();
            if (managedPriority is not ThreadPriority.Highest)
            {
                _state.Failure ??= $"managed_priority_during expected=Highest actual={_state.ManagedDuring}";
                return;
            }

            if (!_owner._native.TryGetSchedulingPolicy(_state.ThreadId, out _state.PolicyDuring, out int error))
            {
                _state.Failure ??= FormatError("sched_getscheduler_during", error);
                return;
            }

            if (!_owner._native.TryGetNice(_state.ThreadId, out _state.NiceDuring, out error))
            {
                _state.Failure ??= FormatError("getpriority_during", error);
                return;
            }

            int expectedNice = _state.AppliedNice ? _state.AppliedNiceValue : _state.NiceBefore;
            if (_state.Failure is null && _state.NiceDuring != expectedNice)
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
                    _state.Failure ??= FormatError("gettid_after", error);
                }
                else if (currentThreadId != _state.ThreadId)
                {
                    _state.Failure ??= $"native_thread_changed before={_state.ThreadId} after={currentThreadId}";
                }
                else
                {
                    if ((_state.Mode is ExpbPriorityMode.Nice or ExpbPriorityMode.Boost) && _state.AppliedNice
                        && !_owner._native.TrySetNice(_state.ThreadId, _state.NiceBefore, out error))
                    {
                        _state.Failure ??= FormatError("setpriority_restore", error);
                    }

                    if (!_owner._native.TryGetSchedulingPolicy(_state.ThreadId, out _state.PolicyAfter, out error))
                    {
                        _state.Failure ??= FormatError("sched_getscheduler_after", error);
                    }

                    if (!_owner._native.TryGetNice(_state.ThreadId, out _state.NiceAfter, out error))
                    {
                        _state.Failure ??= FormatError("getpriority_after", error);
                    }

                    if ((_state.Mode is ExpbPriorityMode.Nice or ExpbPriorityMode.Boost)
                        && _state.NiceAfter != int.MinValue && _state.NiceAfter != _state.NiceBefore)
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
            }

            _state.Failure ??= _state.ManagedDuring is null ? "managed_priority_during not captured" : null;

            _owner.Log(_state);
        }
    }

    internal static string FormatError(string operation, int error)
        => error == NativeApiUnavailableError ? $"{operation} unavailable" : $"{operation} errno={error}";

    private static bool IsPermissionDenied(int error) => error is 1 or 13;

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
        internal bool AppliedNice;
        internal int AppliedNiceValue;
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
        try
        {
            threadId = GetThreadId();
            error = threadId > 0 ? 0 : Marshal.GetLastPInvokeError();
            return threadId > 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            threadId = -1;
            error = ExpbPriorityProbe.NativeApiUnavailableError;
            return false;
        }
    }

    public bool TryGetSchedulingPolicy(int threadId, out int policy, out int error)
    {
        try
        {
            policy = sched_getscheduler(threadId);
            error = policy >= 0 ? 0 : Marshal.GetLastPInvokeError();
            return policy >= 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            policy = -1;
            error = ExpbPriorityProbe.NativeApiUnavailableError;
            return false;
        }
    }

    public bool TryGetNice(int threadId, out int nice, out int error)
    {
        try
        {
            Marshal.SetLastPInvokeError(0);
            nice = getpriority(PrioProcess, threadId);
            error = nice == -1 ? Marshal.GetLastPInvokeError() : 0;
            return nice != -1 || error == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            nice = int.MinValue;
            error = ExpbPriorityProbe.NativeApiUnavailableError;
            return false;
        }
    }

    public bool TrySetNice(int threadId, int nice, out int error)
    {
        try
        {
            int result = setpriority(PrioProcess, threadId, nice);
            error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            return result == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ExpbPriorityProbe.NativeApiUnavailableError;
            return false;
        }
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
