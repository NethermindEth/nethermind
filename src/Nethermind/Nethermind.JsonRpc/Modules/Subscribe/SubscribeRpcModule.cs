// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core.Exceptions;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules.Admin;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    // Lazy: the provider preloads module pools (including this one) in its constructor when PreloadRpcModules is set.
    public class SubscribeRpcModule(ISubscriptionManager subscriptionManager, Lazy<IRpcModuleProvider> moduleProvider) : ISubscribeRpcModule
    {
        public ResultWrapper<string> eth_subscribe(string subscriptionName, string? args = null)
        {
            if (subscriptionName == SubscriptionType.AdminSubscription.PeerEvents
                && moduleProvider.Value.Check(nameof(IAdminRpcModule.admin_subscribe), Context) != ModuleResolution.Enabled)
            {
                return ResultWrapper<string>.Fail($"Wrong subscription type: {subscriptionName}.");
            }

            if (Subscription.ValidateArgs(args) is { } failure) return failure;

            try
            {
                ResultWrapper<string> successfulResult = ResultWrapper<string>.Success(subscriptionManager.AddSubscription(Context.DuplexClient, subscriptionName, args));
                return successfulResult;
            }
            catch (KeyNotFoundException)
            {
                return ResultWrapper<string>.Fail($"Wrong subscription type: {subscriptionName}.");
            }
            catch (LimitExceededException e)
            {
                return ResultWrapper<string>.Fail(e.Message, ErrorCodes.LimitExceeded, isTemporary: true);
            }
            catch (ArgumentException e)
            {
                return ResultWrapper<string>.Fail(e.Message, ErrorCodes.InvalidParams);
            }
            catch (Exception e) when (e is IExceptionWithSafePublicMessage safe)
            {
                return ResultWrapper<string>.Fail(safe.OmitArgumentPosition ? e.Message : $"invalid argument 1: {e.Message}", ErrorCodes.InvalidParams);
            }
            catch (Exception e) when (e is JsonException or FormatException or OverflowException)
            {
                return ResultWrapper<string>.Fail($"Invalid params", ErrorCodes.InvalidParams);
            }
        }

        public ResultWrapper<bool> eth_unsubscribe(string subscriptionId)
        {
            bool unsubscribed = subscriptionManager.RemoveSubscription(Context.DuplexClient, subscriptionId);
            return unsubscribed
                ? ResultWrapper<bool>.Success(true)
                : ResultWrapper<bool>.Fail(ErrorMessages.SubscriptionNotFound, ErrorCodes.ResourceNotFound, isTemporary: true);
        }

        public JsonRpcContext Context { get; set; }


    }
}
