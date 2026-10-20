// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public abstract class Subscription : IDisposable
    {
        protected ILogger _logger;

        /// <param name="jsonRpcDuplexClient">The client notifications are sent to.</param>
        /// <param name="maxQueuedMessages">Queued sends after which a lagging client is disconnected.</param>
        protected Subscription(IJsonRpcDuplexClient jsonRpcDuplexClient, int maxQueuedMessages)
        {
            Span<byte> id = stackalloc byte[16];
            RandomNumberGenerator.Fill(id);
            // A non-zero top nibble keeps the id a uint quantity, which has no leading zeros, at a fixed length.
            id[0] |= 0x10;
            Id = string.Concat("0x", Convert.ToHexStringLower(id));
            JsonRpcDuplexClient = jsonRpcDuplexClient;
            _maxQueuedMessages = maxQueuedMessages;
            SendChannel = Channel.CreateBounded<QueuedSend>(new BoundedChannelOptions(maxQueuedMessages) { SingleReader = true });
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

        /// <summary>
        /// Queue limit for subscriptions that send once per block (a new head, a block's logs): 30 minutes of 2 s blocks.
        /// </summary>
        /// <remarks>
        /// Blocks canonicalised in one burst (a sync batch, a deep rewind) are queued at once, so a burst of more
        /// blocks than this disconnects the client regardless of its speed.
        /// </remarks>
        internal const int MaxQueuedBlocks = 1_000;

        /// <summary>
        /// Queue limit for subscriptions that send once per transaction or p2p message: about 30 s of a busy mempool.
        /// </summary>
        internal const int MaxQueuedEvents = 10_000;

        private readonly int _maxQueuedMessages;
        private volatile bool _overflowed;

        private static readonly Func<object?, Task> InvokeAction = static action => ((Func<Task>)action!)();

        private Channel<QueuedSend> SendChannel { get; }

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

        /// <remarks>
        /// A client that falls the queue limit's worth of sends behind is disconnected rather than buffered
        /// without limit, and its backlog is dropped.
        /// </remarks>
        protected void ScheduleAction(Func<Task> action) => ScheduleAction(InvokeAction, action);

        /// <summary>
        /// Queues <paramref name="send"/> to be invoked with <paramref name="state"/> in order with the other queued sends.
        /// </summary>
        /// <remarks>
        /// A delegate cached by the subscription plus the event's payload as <paramref name="state"/> queues an event
        /// without allocating a closure, and keeps alive only that payload until it is sent.
        /// </remarks>
        protected void ScheduleAction(Func<object?, Task> send, object? state)
        {
            // TryComplete fails once disposed, so only an overflow gets past it.
            if (SendChannel.Writer.TryWrite(new QueuedSend(send, state)) || !SendChannel.Writer.TryComplete()) return;

            _overflowed = true;
            if (_logger.IsWarn) _logger.Warn($"{GetErrorMsg()} Client fell {_maxQueuedMessages} sends behind and is disconnected.");
            // Off the caller's thread, which may be block processing.
            _ = Task.Run(JsonRpcDuplexClient.Dispose);
        }

        protected string GetErrorMsg() => $"{Type} subscription with ID {Id} failed.";

        private void ProcessMessages() => _ = ProcessMessagesAsync();

        private async Task ProcessMessagesAsync()
        {
            try
            {
                while (await SendChannel.Reader.WaitToReadAsync())
                {
                    while (SendChannel.Reader.TryRead(out QueuedSend queued))
                    {
                        if (_overflowed) continue;

                        try
                        {
                            await queued.Send(queued.State);
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

        private readonly record struct QueuedSend(Func<object?, Task> Send, object? State);
    }
}
