// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;

namespace Nethermind.Taiko;

public class TaikoNethermindApi(NethermindApi.Dependencies dependencies) : NethermindApi(dependencies)
{
    public L1OriginStore? L1OriginStore { get; set; }
}
