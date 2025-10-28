// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Types;
public interface IExpCountDown
{
    void Dispose();
    void Reset();
    void SetParams(int initialDuration, int @base, int maxExponent, bool shouldScheduleNext = true);
    void Start(Action callback);
}
