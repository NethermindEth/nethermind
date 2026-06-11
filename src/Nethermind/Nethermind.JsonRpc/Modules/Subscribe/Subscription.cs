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

        /// <summary>
        /// Returns an <c>Invalid params</c> failure when <paramref name="args"/> exceeds
        /// <see cref="JsonRpcLimits.MaxJsonStringArgLength"/>, or <c>null</c> when it is within bounds.
        /// </summary>
        /// <remarks>
        /// Bounds peak memory on the subscribe path before any JSON re-parsing. Called at the
        /// RPC entrypoint so the failure can be surfaced as a normal result rather than via
        /// an exception.
        /// </remarks>
        internal static ResultWrapper<string>? ValidateArgs(string? args) =>
            args is { Length: > JsonRpcLimits.MaxJsonStringArgLength }
                ? ResultWrapper<string>.Fail("Invalid params", ErrorCodes.InvalidParams,
                    $"subscription args string length {args.Length} exceeds maximum allowed length of {JsonRpcLimits.MaxJsonStringArgLength}")
                : null;

        public string Id { get; }
        public abstract string Type { get; }
        public IJsonRpcDuplexClient JsonRpcDuplexClient { get; }
        private Channel<Func<Task>> SendChannel { get; } = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

        public virtual void Dispose() => SendChannel.Writer.TryComplete();

        protected JsonRpcResult CreateSubscriptionMessage<T>(T result, string methodName = SubscriptionMethodName.EthSubscription) => JsonRpcResult.Single(
                new JsonRpcSubscriptionResponse<T>()
                {
                    Params = new JsonRpcSubscriptionResult<T>()
                    {
                        Result = result,
                        Subscription = Id
                    },
                    MethodName = methodName
                }, default);

        protected JsonRpcResult CreateSubscriptionMessage(object result, string methodName = SubscriptionMethodName.EthSubscription) => JsonRpcResult.Single(
                new JsonRpcSubscriptionResponse()
                {
                    Params = new JsonRpcSubscriptionResult()
                    {
                        Result = result,
                        Subscription = Id
                    },
                    MethodName = methodName
                }, default);

        protected void ScheduleAction(Func<Task> action) => SendChannel.Writer.TryWrite(action);

        protected string GetErrorMsg() => $"{Type} subscription with ID {Id} failed.";

        private void ProcessMessages() => _ = ProcessMessagesAsync();

        private async Task ProcessMessagesAsync()
        {
            try
            {
                while (await SendChannel.Reader.WaitToReadAsync())
                {
                    while (SendChannel.Reader.TryRead(out Func<Task>? action))
                    {
                        try
                        {
                            await action!();
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{GetErrorMsg()} With exception {e}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"{GetErrorMsg()} {nameof(ProcessMessages)} encountered an exception.", e);
            }
        }
    }
}
