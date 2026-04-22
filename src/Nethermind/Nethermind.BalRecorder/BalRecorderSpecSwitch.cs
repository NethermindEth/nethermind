// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.BalRecorder;

public class BalRecorderSpecSwitch
{
    private readonly AsyncLocal<bool?> _enabled = new();
    public bool Enabled { get => _enabled.Value is true; set => _enabled.Value = value ? true : null; }
}
