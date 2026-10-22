// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

#pragma warning disable IDE0130 // Preserve the upstream protocol namespace and log category.
namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/multiformats/multistream-select
/// </summary>
/// <remarks>Backports rejected-request completion until a fixed Multistream package is available.
/// <see href="https://github.com/NethermindEth/dotnet-libp2p/commit/97872a15c877e2f1df0030c9a7ef7b54fc5823f7">Upstream fix</see>.</remarks>
internal class MultistreamProtocol(ILoggerFactory? loggerFactory = null) : IConnectionProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultistreamProtocol>();
    private const string ProtocolNotSupported = "na";

    public string Id => "/multistream/1.0.0";

    public async Task DialAsync(IChannel channel, IConnectionContext context)
    {
        _logger?.LogTrace("Hello started");

        IProtocol? selectedProtocol = context.UpgradeOptions?.SelectedProtocol;
        if (selectedProtocol is not null)
        {
            if (!await DialSelectedProtocolOptimistically(channel, selectedProtocol))
            {
                _logger?.LogDebug("Negotiation failed");
                context.UpgradeOptions?.CompletionSource?.TrySetException(new ChannelClosedException());
                return;
            }

            _logger?.LogDebug("Protocol selected during dialing: {Id}", selectedProtocol.Id);
            await context.Upgrade(channel, selectedProtocol);
            return;
        }

        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            _logger?.LogTrace("Hello failed");
            return;
        }
        _logger?.LogTrace("Hello passed");

        async Task<bool?> DialProtocol(IProtocol selector)
        {
            try
            {
                _logger?.LogTrace("DialProtocol: proposing {Id}", selector.Id);
                await channel.WriteLineAsync(selector.Id);
                string selectorLine = await channel.ReadLineAsync();
                _logger?.LogTrace("Proposed {Id}, answer: {Answer}", selector.Id, selectorLine ?? "(null)");
                if (selectorLine == selector.Id)
                {
                    return true;
                }

                if (selectorLine != ProtocolNotSupported)
                {
                    return false;
                }

                return null;
            }
            catch (ChannelClosedException)
            {
                _logger?.LogWarning("Channel closed during protocol negotiation (dial, selector {Id}).", selector.Id);
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Protocol negotiation canceled (dial, selector {Id}).", selector.Id);
                return false;
            }
        }

        IProtocol? selected = null;

        foreach (IProtocol selector in context!.SubProtocols)
        {
            bool? dialResult = await DialProtocol(selector);
            if (dialResult == true)
            {
                selected = selector;
                break;
            }
            else if (dialResult == false)
            {
                break;
            }
        }

        if (selected is null)
        {
            _logger?.LogDebug("Negotiation failed");
            return;
        }
        _logger?.LogDebug("Protocol selected during dialing: {Id}", selected.Id);
        await context.Upgrade(channel, selected);
    }

    public async Task ListenAsync(IChannel channel, IConnectionContext context)
    {
        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            return;
        }

        IProtocol? selected = null;
        try
        {
            for (; ; )
            {
                _logger?.LogTrace("Listen: waiting for remote protocol proposal");
                string proto = await channel.ReadLineAsync();
                _logger?.LogTrace("Listen: proposed by remote {Proto}", proto ?? "(null)");
                selected = context.SubProtocols.FirstOrDefault(x => x.Id == proto) as IProtocol;
                if (selected is not null)
                {
                    await channel.WriteLineAsync(selected.Id);
                    _logger?.LogTrace("Proposed by remote {Proto}, answer: {Selected}", proto, selected.Id);
                    break;
                }

                _logger?.LogTrace("Proposed by remote {Proto}, answer: {Na}", proto, ProtocolNotSupported);
                await channel.WriteLineAsync(ProtocolNotSupported);
            }
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("Channel closed during multistream protocol negotiation (listen).");
            return;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Multistream protocol negotiation canceled (listen).");
            return;
        }

        if (selected is null)
        {
            _logger?.LogDebug("Negotiation failed");
            return;
        }

        _logger?.LogDebug("Protocol selected during listening: {Selected}", selected.Id);
        await context.Upgrade(channel, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        try
        {
            _logger?.LogTrace("SendHello: sending multistream id");
            await channel.WriteLineAsync(Id);
            string line = await channel.ReadLineAsync();
            _logger?.LogTrace("SendHello: received {Line}", line ?? "(null)");
            return line == Id;
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("Channel closed during multistream hello.");
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Multistream hello canceled.");
            return false;
        }
    }

    private async Task<bool> DialSelectedProtocolOptimistically(IChannel channel, IProtocol selectedProtocol)
    {
        try
        {
            _logger?.LogTrace("SendHello: sending multistream id");
            await channel.WriteLineAsync(Id);
            _logger?.LogTrace("DialProtocol: optimistically proposing {Id}", selectedProtocol.Id);
            await channel.WriteLineAsync(selectedProtocol.Id);

            string line = await channel.ReadLineAsync();
            _logger?.LogTrace("SendHello: received {Line}", line ?? "(null)");
            if (line != Id)
            {
                return false;
            }

            string selectorLine = await channel.ReadLineAsync();
            _logger?.LogTrace("Proposed {Id}, answer: {Answer}", selectedProtocol.Id, selectorLine ?? "(null)");
            return selectorLine == selectedProtocol.Id;
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("Channel closed during optimistic protocol negotiation (dial, selector {Id}).", selectedProtocol.Id);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Optimistic protocol negotiation canceled (dial, selector {Id}).", selectedProtocol.Id);
            return false;
        }
    }
}
