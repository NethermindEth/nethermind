// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Optimism.Rpc;

[RpcModule(ModuleType.Eth)]
public interface IOptimismEthRpcModule : IEthRpcModule;
