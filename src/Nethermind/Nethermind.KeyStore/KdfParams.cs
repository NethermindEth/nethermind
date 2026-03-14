// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.KeyStore
{
    public class KdfParams
    {
        [JsonPropertyName("dklen")]
        public int DkLen { get; set; }

        public string Salt { get; set; }

        public int? N { get; set; }

        public int? R { get; set; }

        public int? P { get; set; }

        public int? C { get; set; }

        public string Prf { get; set; }
    }
}
