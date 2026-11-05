// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.BalRecorder;

public class BalRecorderReleaseSpec(IReleaseSpec inner, BalRecorderSpecSwitch balSwitch)
    : ReleaseSpecDecorator(inner)
{
    public override bool IsEip7928Enabled => balSwitch.Enabled || base.IsEip7928Enabled;
}
