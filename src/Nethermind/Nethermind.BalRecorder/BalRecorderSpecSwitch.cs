// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.BalRecorder;

public class BalRecorderSpecSwitch
{
    // bool? so that disabling (set null) removes the entry from the ExecutionContext dictionary
    // rather than leaving an explicit false, avoiding a small per-block EC allocation when disabled.
    // Block processing is synchronous so child-flow propagation semantics are not a concern here.
    private readonly AsyncLocal<bool?> _enabled = new();
    public bool Enabled { get => _enabled.Value is true; set => _enabled.Value = value ? true : null; }
}
