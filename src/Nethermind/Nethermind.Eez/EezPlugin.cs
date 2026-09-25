// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Eez.Config;
using Nethermind.Eez.Rpc;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Eez;

public class EezPlugin(ChainSpec chainSpec, IEezConfig eezConfig) : INethermindPlugin
{
    public string Name => "Eez";
    public string Description => "EEZ rollup L2 execution rules";
    public string Author => "Nethermind";
    public bool Enabled => eezConfig.Enabled;
    public bool MustInitialize => true;

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        EnsureEezGenesis(chainSpec);
        api.RegisterTxType<EezSystemTransactionForRpc>(EezTxType.CreateDecoder(), EezTxType.CreateValidator(api.SpecProvider!.ChainId));
    }

    public IModule Module => new EezModule();

    internal static void EnsureEezGenesis(ChainSpec chainSpec)
    {
        ChainSpecAllocation? eezl2 = chainSpec.Allocations is { } allocations
            && allocations.TryGetValue(EezConstants.Eezl2Address, out ChainSpecAllocation? allocation)
                ? allocation
                : null;
        if (eezl2?.Code is not { Length: > 0 })
        {
            throw new InvalidConfigurationException(
                $"{nameof(IEezConfig)}.{nameof(IEezConfig.Enabled)} requires an EEZ genesis with the EEZL2 predeploy at {EezConstants.Eezl2Address}.",
                ExitCodes.ConflictingConfigurations);
        }
    }
}
