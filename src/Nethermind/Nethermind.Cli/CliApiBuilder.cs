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
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Nethermind.Cli
{
    public class CliApiBuilder
    {
        private readonly Engine _engine;
        private readonly ObjectInstance _instance;

        public CliApiBuilder(Engine engine, string name)
        {
            _engine = engine;
            _instance = _engine.Object.Construct(Arguments.Empty);
            _engine.SetValue(name, _instance);
        }

        public CliApiBuilder AddMethod<T1, T2>(string name, Action<T1, T2> action)
        {
            return AddMethod(name, new DelegateWrapper(_engine, action));
        }
        
        public CliApiBuilder AddMethod<T>(string name, Action<T> action)
        {
            return AddMethod(name, new DelegateWrapper(_engine, action));
        }
        
        public CliApiBuilder AddMethod(string name, Action action)
        {
            return AddMethod(name, new DelegateWrapper(_engine, action));
        }
        
        public CliApiBuilder AddMethod<T>(string name, Func<T> func)
        {
            return AddMethod(name, new DelegateWrapper(_engine, func));
        }
        
        public CliApiBuilder AddProperty<T>(string name, Func<T> func)
        {
            var nativeDelegate = new DelegateWrapper(_engine, func);
            JsValue getter = JsValue.FromObject(_engine, nativeDelegate);
            JsValue setter = JsValue.Null;

            _instance.DefineOwnProperty(name, new PropertyDescriptor(getter, setter, true, false), true);
            return this;
        }
        
        public CliApiBuilder AddMethod<T, TResult>(string name, Func<T, TResult> func)
        {
            return AddMethod(name, new DelegateWrapper(_engine, func));
        }

        private CliApiBuilder AddMethod(string name, DelegateWrapper delegateWrapper)
        {
            _instance.FastAddProperty(name, delegateWrapper, true, false, true);
            return this;
        }

        public void Build()
        {
        }
    }
}