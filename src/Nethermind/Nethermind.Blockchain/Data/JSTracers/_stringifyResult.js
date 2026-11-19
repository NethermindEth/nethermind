// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

(function () {
    'use strict';
    const stringify = JSON.stringify;
    const keys = Object.keys;
    const create = Object.create;
    const isArray = Array.isArray;
    const apply = Reflect.apply;
    const toString = String;
    const floor = Math.floor;
    const WeakSetConstructor = WeakSet;
    const has = Function.prototype.call.bind(WeakSet.prototype.has);
    const add = Function.prototype.call.bind(WeakSet.prototype.add);
    const remove = Function.prototype.call.bind(WeakSet.prototype.delete);
    const brands = [Number.prototype.valueOf, String.prototype.valueOf, Boolean.prototype.valueOf, BigInt.prototype.valueOf];

    function prepare(holder, key) {
        let item = holder[key];
        if (item !== null && (typeof item === 'object' || typeof item === 'function' || typeof item === 'bigint')) {
            const toJSON = item.toJSON;
            if (typeof toJSON === 'function') item = apply(toJSON, item, [key]);
        }
        if (item !== null && typeof item === 'object' && !isArray(item)) {
            // Intrinsic brand checks do not run user coercion or Symbol.toStringTag getters.
            for (let i = 0; i < brands.length; i++) {
                let matched = false;
                let unboxed;
                try { unboxed = apply(brands[i], item, []); matched = true; } catch (_) { }
                if (matched) return i === 0 ? +item : i === 1 ? toString(item) : unboxed;
            }
        }
        return item;
    }

    return function (value, maxDepth, markDepth) {
        const frames = create(null);
        const active = new WeakSetConstructor();
        let depth = 0;
        let output = '';
        let holder = create(null);
        holder[''] = value;
        let key = '';
        for (;;) {
            const item = prepare(holder, key);
            const omitted = item === undefined || typeof item === 'function' || typeof item === 'symbol';
            if (omitted) {
                if (!depth || frames[depth - 1].array) output += 'null';
            } else {
                if (depth && !frames[depth - 1].array) {
                    const parent = frames[depth - 1];
                    if (!parent.first) output += ',';
                    parent.first = false;
                    output += stringify(key) + ':';
                }
                if (item !== null && typeof item === 'object') {
                    if (depth >= maxDepth || has(active, item)) {
                        markDepth();
                        throw null;
                    }
                    add(active, item);
                    const array = isArray(item);
                    const names = array ? null : keys(item);
                    let length = array ? floor(+item.length) : names.length;
                    if (!(length > 0)) length = 0;
                    if (length > 9007199254740991) length = 9007199254740991;
                    frames[depth++] = { object: item, array: array, names: names, length: length, index: 0, first: true };
                    output += array ? '[' : '{';
                } else {
                    output += stringify(item);
                }
            }
            while (depth) {
                const frame = frames[depth - 1];
                if (frame.index < frame.length) {
                    holder = frame.object;
                    if (frame.array) {
                        if (frame.index) output += ',';
                        key = toString(frame.index++);
                    } else {
                        key = frame.names[frame.index++];
                    }
                    break;
                }
                output += frame.array ? ']' : '}';
                remove(active, frame.object);
                delete frames[--depth];
            }
            if (!depth) return output;
        }
    };
})()
