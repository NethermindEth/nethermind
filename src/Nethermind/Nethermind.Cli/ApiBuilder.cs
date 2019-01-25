/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Cli
{
    public class CliApiBuilder
    {
        private ILogManager _logManager;
        private IJsonSerializer _serializer;
        private IJsonRpcClient _client;

        private readonly Engine _engine;
        private string _apiName;
        private ObjectInstance _instance;

        public CliApiBuilder(Engine engine, IJsonSerializer jsonSerializer, IJsonRpcClient client, ILogManager logManager)
        {
            _engine = engine;
            _serializer = jsonSerializer;
            _client = client;
            _logManager = logManager;
        }

        public CliApiBuilder Api(string name)
        {
            _apiName = name;
            _instance = _engine.Object.Construct(Arguments.Empty);
            _engine.SetValue(name, _instance);
            return this;
        }

        public CliApiBuilder WithAction<T1, T2>(string name, Action<T1, T2> action)
        {
            return WithMethod(name, new DelegateWrapper(_engine, action));
        }

        public CliApiBuilder WithFunc<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> func)
        {
            return WithMethod(name, new DelegateWrapper(_engine, func));
        }
        
        public CliApiBuilder WithFunc<T1, T2, TResult>(string name, Func<T1, T2, TResult> func)
        {
            return WithMethod(name, new DelegateWrapper(_engine, func));
        }

        public CliApiBuilder WithAction<T>(string name, Action<T> action)
        {
            return WithMethod(name, new DelegateWrapper(_engine, action));
        }

        public CliApiBuilder WithAction(string name, Action action)
        {
            return WithMethod(name, new DelegateWrapper(_engine, action));
        }

        public CliApiBuilder WithFunc<T>(string name, Func<T> func)
        {
            return WithMethod(name, new DelegateWrapper(_engine, func));
        }

        public CliApiBuilder WithProperty<T>(string name, Func<T> func)
        {
            var nativeDelegate = new DelegateWrapper(_engine, func);
            return AddProperty(name, nativeDelegate);
        }

        public CliApiBuilder WithFunc<T, TResult>(string name, Func<T, TResult> func)
        {
            return WithMethod(name, new DelegateWrapper(_engine, func));
        }

        public CliApiBuilder WithFunc<TResult>(string name)
        {
            string apiName = _apiName;
            Func<object[], string> rpcCall = parameters => _serializer.Serialize(_client.Post<TResult>($"{apiName}_{name}", parameters).Result, true);
            return WithMethod(name, new DelegateWrapper(_engine, rpcCall));
        }

        public CliApiBuilder WithFunc<T1, TResult>(string name)
        {
            string apiName = _apiName;
            Func<T1, string> rpcCall = parameters => _serializer.Serialize(_client.Post<TResult>($"{apiName}_{name}", parameters).Result, true);
            return WithMethod(name, new DelegateWrapper(_engine, rpcCall));
        }

        public CliApiBuilder WithFunc<T1, T2, TResult>(string name)
        {
            string apiName = _apiName;
            Func<T1, T2, TResult> rpcCall = (parameter1, parameter2) => _client.Post<TResult>($"{apiName}_{name}", parameter1, parameter2).Result;
            return WithMethod(name, new DelegateWrapper(_engine, rpcCall));
        }

        public CliApiBuilder WithProperty<TResult>(string name)
        {
            string apiName = _apiName;
            Func<TResult> rpcCall = () => _client.Post<TResult>($"{apiName}_{name}").Result;
            return AddProperty(name, new DelegateWrapper(_engine, rpcCall));
        }

        private CliApiBuilder WithMethod(string name, DelegateWrapper delegateWrapper)
        {
            _instance.FastAddProperty(name, delegateWrapper, true, false, true);
            return this;
        }

        private CliApiBuilder AddProperty(string name, DelegateWrapper delegateWrapper)
        {
            JsValue getter = JsValue.FromObject(_engine, delegateWrapper);
            JsValue setter = JsValue.Null;

            _instance.DefineOwnProperty(name, new PropertyDescriptor(getter, setter, true, false), true);
            return this;
        }

        public void Build()
        {
            _instance = null;
            _apiName = null;
        }
    }
}