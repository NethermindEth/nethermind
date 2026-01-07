// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Consensus.Producers;

/*
* This class was introduced because of the merge changes.
* PreMerge starting block production depended on the flag from mining config.
* However, in the post-merge world, our node might not be miner pre-merge, and it is a validator after the merge. Generally, in post-merge, we should always start a block production logic. If we weren't pre-merge miner merge plugin will be able to wrap null as a preMergeBlockProducer.
* To resolve this problem BlockProductionPolicy was introduced.
 */
public class BlockProductionPolicy(IMiningConfig miningConfig) : IBlockProductionPolicy
{
    public bool ShouldStartBlockProduction() => miningConfig.Enabled;
}

public class NeverStartBlockProductionPolicy : IBlockProductionPolicy
{
    public bool ShouldStartBlockProduction() => false;

    public static NeverStartBlockProductionPolicy Instance =>
        LazyInitializer.EnsureInitialized(ref _instance, static () => new());

    private static NeverStartBlockProductionPolicy? _instance;
    private NeverStartBlockProductionPolicy() { }
}

public class AlwaysStartBlockProductionPolicy : IBlockProductionPolicy
{
    public bool ShouldStartBlockProduction() => true;

    public static AlwaysStartBlockProductionPolicy Instance =>
        LazyInitializer.EnsureInitialized(ref _instance, static () => new());

    private static AlwaysStartBlockProductionPolicy? _instance;
    private AlwaysStartBlockProductionPolicy() { }
}

public interface IBlockProductionPolicy
{
    bool ShouldStartBlockProduction();
}
