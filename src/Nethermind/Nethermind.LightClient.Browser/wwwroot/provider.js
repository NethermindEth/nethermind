// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

/** Creates a read-only EIP-1193 provider with verification in a dedicated worker. */
export async function createProvider(options) {
    const worker = new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
    const provider = new LightClientProvider(worker);
    try {
        const result = await provider.send('initialize', options);
        provider.connected = true;
        provider.chainId = result.chainId;
        queueMicrotask(() => provider.emit('connect', { chainId: provider.chainId }));
        return provider;
    } catch (error) {
        provider.disconnect();
        throw error;
    }
}

class LightClientProvider {
    constructor(worker) {
        this.worker = worker;
        this.pending = new Map();
        this.listeners = new Map();
        this.sequence = 0;
        this.connected = false;
        worker.onmessage = ({ data }) => {
            if (data.event) {
                this.emit(data.event, data.data);
                return;
            }
            const request = this.pending.get(data.id);
            if (!request) return;
            this.pending.delete(data.id);
            clearTimeout(request.timeout);
            if (data.error) request.reject(Object.assign(new Error(data.error.message), data.error));
            else request.resolve(data.result);
        };
        worker.onerror = () => this.disconnect(new Error('Browser verification worker failed.'));
    }

    request({ method, params = [] } = {}) {
        if (typeof method !== 'string' || !Array.isArray(params)) {
            return Promise.reject(Object.assign(new Error('Invalid provider request.'), { code: -32602 }));
        }
        if (!this.connected) return Promise.reject(Object.assign(new Error('Light client is disconnected.'), { code: 4900 }));
        return this.send('request', { method, params });
    }

    send(action, parameters) {
        if (this.pending.size >= 16) {
            return Promise.reject(Object.assign(new Error('Too many browser requests.'), { code: -32005 }));
        }
        let encoded;
        try { encoded = JSON.stringify(parameters); }
        catch {
            return Promise.reject(Object.assign(new Error('Provider parameters must be JSON values.'), { code: -32602 }));
        }
        if (encoded === undefined || encoded.length > 1024 * 1024) {
            return Promise.reject(Object.assign(new Error('Browser request exceeds its size limit.'), { code: -32602 }));
        }
        const id = ++this.sequence;
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                this.pending.delete(id);
                reject(Object.assign(new Error('Browser request timed out.'), { code: -32000 }));
            }, action === 'initialize' ? 180_000 : 90_000);
            this.pending.set(id, { resolve, reject, timeout });
            try { this.worker.postMessage({ id, action, parameters: JSON.parse(encoded) }); }
            catch (error) {
                clearTimeout(timeout);
                this.pending.delete(id);
                reject(error);
            }
        });
    }

    on(event, listener) {
        if (typeof listener !== 'function') throw new TypeError('Provider listener must be a function.');
        let listeners = this.listeners.get(event);
        if (!listeners) this.listeners.set(event, listeners = new Set());
        listeners.add(listener);
        return this;
    }

    removeListener(event, listener) {
        this.listeners.get(event)?.delete(listener);
        return this;
    }

    emit(event, data) {
        for (const listener of this.listeners.get(event) ?? []) listener(data);
    }

    disconnect(reason = new Error('Light client was disconnected.')) {
        const wasConnected = this.connected;
        this.connected = false;
        this.worker.terminate();
        for (const pending of this.pending.values()) {
            clearTimeout(pending.timeout);
            pending.reject(Object.assign(reason, { code: 4900 }));
        }
        this.pending.clear();
        if (wasConnected) this.emit('disconnect', { code: 4900, message: reason.message });
    }
}
