// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text;
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

        /// <summary>The UTF-8 JSON of the request's <c>params</c> as read from the transport; empty when <see cref="Params"/> was set instead.</summary>
        /// <remarks>Borrows the transport's buffer: valid only until the request's processing completes; copy it to retain it.</remarks>
        public ReadOnlyMemory<byte> ParamsUtf8 { get; internal set; }
        internal JsonValueKind ParamsKind { get; set; }

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

        internal string MethodForLogging => EncodeLogText(Method);

        internal static string EncodeLogText(string? value)
        {
            if (value is null) return string.Empty;

            StringBuilder? escaped = null;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (!char.IsControl(character) && character is not ('\u2028' or '\u2029')) continue;

                escaped ??= new(value.Length);
                escaped.Append(value.AsSpan(start, i - start));
                switch (character)
                {
                    case '\r': escaped.Append(@"\r"); break;
                    case '\n': escaped.Append(@"\n"); break;
                    case '\t': escaped.Append(@"\t"); break;
                    default: escaped.Append(CultureInfo.InvariantCulture, $@"\u{(ushort)character:X4}"); break;
                }
                start = i + 1;
            }

            return escaped?.Append(value.AsSpan(start)).ToString() ?? value;
        }

        public override string ToString() => $"Id:{Id}, {MethodForLogging}(params omitted)";
    }
}
