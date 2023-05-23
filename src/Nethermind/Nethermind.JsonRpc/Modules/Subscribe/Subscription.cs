// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public abstract class Subscription : IDisposable
    {
        protected ILogger _logger;

        protected Subscription(IJsonRpcDuplexClient jsonRpcDuplexClient)
        {
            Id = string.Concat("0x", Guid.NewGuid().ToString("N"));
            JsonRpcDuplexClient = jsonRpcDuplexClient;
            ProcessMessages();
        }

        public string Id { get; }
        public abstract string Type { get; }
        public IJsonRpcDuplexClient JsonRpcDuplexClient { get; }
        private Channel<Action> SendChannel { get; } = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions() { SingleReader = true });

        public virtual void Dispose()
        {
            SendChannel.Writer.Complete();
        }

        protected JsonRpcResult CreateSubscriptionMessage(object result)
        {
            return JsonRpcResult.Single(
                new JsonRpcSubscriptionResponse()
                {
                    Params = new JsonRpcSubscriptionResult()
                    {
                        Result = result,
                        Subscription = Id
                    }
                }, default);
        }

        protected void ScheduleAction(Action action)
        {
            SendChannel.Writer.TryWrite(action);
        }

        protected string GetErrorMsg() => $"{Type} subscription with ID {Id} failed.";

        private void ProcessMessages()
        {
            Task.Factory.StartNew(async () =>
            {
                while (await SendChannel.Reader.WaitToReadAsync())
                {
                    while (SendChannel.Reader.TryRead(out Action action))
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{GetErrorMsg()} With exception {e}");
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error($"{GetErrorMsg()} {nameof(ProcessMessages)} encountered an exception.", t.Exception);
                }
            });
        }
    }
}
