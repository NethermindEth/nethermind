// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc;
public interface ITimeoutTimer
{
    void Reset(TimeSpan period);
    void Start(TimeSpan period);
    void TriggerTimeout();
}
