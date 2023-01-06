// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin.EngineApi.Paris.Data;

public interface IGetPayloadResult
{
    public IBlockProductionContext Block { set; }
}
