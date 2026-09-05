// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public class JsonRpcRequest
    {
        private JsonElement _params;
        private JsonDocument? _paramsDocument;
        private JsonRpcId _id;
        private bool _paramsSet;

        public string JsonRpc { get; set; }
        public string Method { get; set; }

        public JsonElement Params
        {
            get
            {
                if (!_paramsSet && !ParamsUtf8.IsEmpty)
                {
                    _paramsDocument = JsonDocument.Parse(ParamsUtf8);
                    _params = _paramsDocument.RootElement;
                    _paramsSet = true;
                    ParamsKind = _params.ValueKind;
                }

                return _params;
            }
            set
            {
                _params = value;
                _paramsSet = value.ValueKind != JsonValueKind.Undefined;
                ParamsKind = value.ValueKind;
            }
        }

        internal ReadOnlyMemory<byte> ParamsUtf8 { get; set; }
        internal JsonValueKind ParamsKind { get; set; }

        /// <summary>Byte length of the raw <c>params</c> element, or zero when the request carries none.</summary>
        /// <remarks>
        /// Known without materializing the parameters on either input path: the body slice when the request was read
        /// straight from the body, the document's backing buffer when it was parsed into a <see cref="JsonDocument"/>.
        /// </remarks>
        internal int ParamsUtf8Length => !ParamsUtf8.IsEmpty
            ? ParamsUtf8.Length
            : _params.ValueKind == JsonValueKind.Undefined ? 0 : JsonMarshal.GetRawUtf8Value(_params).Length;

        internal void DisposeParsedParamsDocument()
        {
            _paramsDocument?.Dispose();
            _paramsDocument = null;
            if (!_paramsSet && !ParamsUtf8.IsEmpty)
            {
                ParamsUtf8 = default;
                _paramsSet = true;
            }
        }

        [JsonConverter(typeof(JsonRpcIdConverter))]
        public JsonRpcId Id { get => _id; set => _id = value; }

        internal ref readonly JsonRpcId IdRef => ref _id;

        public override string ToString() => $"Id:{Id}, {Method}({Params})";
    }
}
