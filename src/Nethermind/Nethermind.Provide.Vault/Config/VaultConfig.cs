// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Vault.Config
{
    public class VaultConfig : IVaultConfig
    {
        public bool Enabled { get; set; }
        public string Host { get; set; } = "vault.provide.services";
        public string Token { get; set; }
        public string Scheme { get; set; } = "https";
        public string Path { get; set; } = "api/v1";
        public string VaultId { get; set; }
        public string VaultKeyFile { get; set; }
        public string VaultSealUnsealKey { get; set; }
        public string NChainHost { get; set; }
        public string NChainToken { get; set; }
        public string NChainScheme { get; set; }
        public string NChainPath { get; set; }
        public string NChainNetworkId { get; set; }
        public string NChainAccountId { get; set; }
    }
}
