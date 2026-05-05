// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Ethereum.Test.Base
{
    public class EthereumTestResult
    {
        public EthereumTestResult(string? name, string? fork, bool pass)
        {
            Name = name ?? "unnamed";
            Fork = fork ?? "unknown";
            Pass = pass;
        }

        public EthereumTestResult(string? name, string? fork, string loadFailure)
        {
            Name = name ?? "unnamed";
            Fork = fork ?? "unknown";
            Pass = false;
            LoadFailure = loadFailure;
        }

        public EthereumTestResult(string? name, string? loadFailure)
            : this(name, null, loadFailure)
        {
        }

        public string? LoadFailure { get; set; }
        public string Name { get; set; }
        public bool Pass { get; set; }
        public string Fork { get; set; }

        public double TimeInMs { get; set; }

        public Hash256 StateRoot { get; set; } = Keccak.EmptyTreeHash;
    }
}
