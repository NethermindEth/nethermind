// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NLog;
using NLog.Targets;

namespace Nethermind.Fuzzer.Plugin;

[Target(TargetName)]
internal sealed class FuzzerCaptureTarget : TargetWithLayout
{
    public const string TargetName = "FuzzerCapture";

    private readonly FuzzerRuntime _runtime;

    public FuzzerCaptureTarget(FuzzerRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Layout = "${message}";
    }

    protected override void Write(LogEventInfo logEvent)
    {
        _runtime.Handle(logEvent);
    }
}
