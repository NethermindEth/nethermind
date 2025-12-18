// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;

public sealed class NullDiagnosticListener : DiagnosticListener
{
    public static NullDiagnosticListener Instance { get; } = new("");
    public NullDiagnosticListener(string name) : base(name) { }
    public override void Dispose() { }
    public override bool IsEnabled(string name) => false;
    public override bool IsEnabled(string name, object? arg1, object? arg2 = null) => false;
    public override void OnActivityExport(Activity activity, object? payload) { }
    public override void OnActivityImport(Activity activity, object? payload) { }
    public override IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer) => NullDisposable.Instance;
    public override IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer, Func<string, object?, object?, bool>? isEnabled) => NullDisposable.Instance;
    public override IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer, Predicate<string>? isEnabled) => NullDisposable.Instance;
    public override IDisposable Subscribe(IObserver<KeyValuePair<string, object?>> observer, Func<string, object?, object?, bool>? isEnabled, Action<Activity, object?>? onActivityImport = null, Action<Activity, object?>? onActivityExport = null) => NullDisposable.Instance;
    public override void Write(string name, object? value) { }
    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
