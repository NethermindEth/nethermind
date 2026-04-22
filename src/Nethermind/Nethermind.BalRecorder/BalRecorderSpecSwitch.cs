// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.BalRecorder;

public class BalRecorderSpecSwitch
{
    // bool? so that disabling sets null (the default), which removes the entry from the ExecutionContext
    // dictionary rather than storing an explicit false. Child async flows then have no entry and correctly
    // see Enabled = false without inheriting a stale explicit override.
    private readonly AsyncLocal<bool?> _enabled = new();
    public bool Enabled { get => _enabled.Value is true; set => _enabled.Value = value ? true : null; }
}
