// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Taiko.Tdx;

[RpcModule(ModuleType.Eth)]
public interface ITdxRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns a TDX attestation for the specified block.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<TdxAttestation?>> taiko_getTdxAttestation(Hash256 blockHash);

    [JsonRpcMethod(
        Description = "Returns the TDX guest information for instance registration.",
        IsSharable = true,
        IsImplemented = true)]
    Task<ResultWrapper<TdxGuestInfo?>> taiko_getTdxGuestInfo();

    [JsonRpcMethod(
        Description = "Bootstraps the TDX service (generates key, gets initial quote).",
        IsSharable = false,
        IsImplemented = true)]
    Task<ResultWrapper<TdxGuestInfo?>> taiko_tdxBootstrap();
}

