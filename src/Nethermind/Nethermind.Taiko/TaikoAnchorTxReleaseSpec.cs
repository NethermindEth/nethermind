// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Taiko;

public class TaikoAnchorTxReleaseSpec(IReleaseSpec parent, Address? eip1559FeeCollector) : ReleaseSpecDecorator(parent), IEip1559Spec
{
    public override Address? Eip1559FeeCollector => eip1559FeeCollector;
}
