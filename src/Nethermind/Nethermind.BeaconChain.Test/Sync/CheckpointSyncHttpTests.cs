// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// Every public provider serves the finalized checkpoint over the beacon API, labelled with its fork in the
/// Eth-Consensus-Version header; refusing the Gloas label refuses every checkpoint once Gloas finalizes.
/// </summary>
public class CheckpointSyncHttpTests
{
    [TestCase("gloas")]
    [TestCase("Gloas")]
    [TestCase(null, Description = "Without the header the state's slot selects its layout.")]
    public async Task A_gloas_checkpoint_served_over_the_beacon_api_is_anchored_in_the_gloas_shape(string? consensusVersion)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        await using WebApplication provider = await StartProviderAsync(consensusVersion);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.State, Is.TypeOf<ForkedBeaconState.OfGloas>());
            Assert.That(anchor.Block, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>(), "the block endpoint answered for the derived anchor root");
            Assert.That(anchor.BlockRoot, Is.EqualTo(first.Root));
            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out _), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(first.Root));
        }
    }

    [Test]
    public async Task A_checkpoint_labelled_with_an_unknown_fork_is_refused_before_anything_is_persisted()
    {
        await using WebApplication provider = await StartProviderAsync("heze");
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        NotSupportedException ex = Assert.ThrowsAsync<NotSupportedException>(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(ex.Message, Does.Contain("'heze'"));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    /// <summary>A beacon API serving <see cref="ForkCrossingChain.First"/> as the finalized state and its block by root.</summary>
    private static async Task<WebApplication> StartProviderAsync(string? consensusVersion)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        byte[] stateSsz = BeaconStateGloas.Encode(first.PostState);
        byte[] blockSsz = SignedBeaconBlockCodec.Encode(new ForkedSignedBeaconBlock.OfGloas(first.Block), GloasCheckpointFiles.Spec);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.MapGet("/eth/v2/debug/beacon/states/finalized", async (HttpContext c) =>
        {
            if (consensusVersion is not null)
            {
                c.Response.Headers["Eth-Consensus-Version"] = consensusVersion;
            }

            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(stateSsz);
        });
        app.MapGet("/eth/v2/beacon/blocks/{root}", async (HttpContext c, string root) =>
        {
            if (root != first.Root.ToString())
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(blockSsz);
        });
        await app.StartAsync();
        return app;
    }
}
