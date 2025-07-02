// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;

namespace Nethermind.Taiko;

public class TaikoNethermindApi(NethermindApi.Dependencies dependencies) : NethermindApi(dependencies)
{
    public IL1OriginStore L1OriginStore => Context.Resolve<IL1OriginStore>();
}
