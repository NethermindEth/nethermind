// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Overseer.Test.JsonRpc.Dto
{
    public class DataAssetDto
    {
        // Default ID when adding a new data asset (otherwise, will fail for null).
        public string Id { get; set; } = "0xd45c6b02474e7c60aeaf60df4ee451a53a09bb5df0a7e9231a0def145785f086";
        public string Name { get; set; }
        public string Description { get; set; }
        public string UnitPrice { get; set; }
        public string UnitType { get; set; }
        public uint MinUnits { get; set; }
        public uint MaxUnits { get; set; }
        public DataAssetRulesDto Rules { get; set; }
        public DataAssetProviderDto Provider { get; set; }
        public string File { get; set; }
        public byte[] Data { get; set; }
    }
}
