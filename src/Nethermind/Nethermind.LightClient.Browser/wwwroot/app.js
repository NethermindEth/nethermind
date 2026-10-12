// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import { createProvider } from './provider.js';

const status = document.querySelector('#status');
const result = document.querySelector('#result');
document.querySelector('#relays').value = location.origin;
let provider;

document.querySelector('#connect').addEventListener('submit', async event => {
    event.preventDefault();
    const button = document.querySelector('#start');
    button.disabled = true;
    document.querySelector('#balance').classList.add('hidden');
    result.classList.add('hidden');
    status.textContent = 'Starting the verification worker and authenticating consensus…';
    try {
        provider?.disconnect();
        provider = await createProvider({
            network: document.querySelector('#network').value,
            checkpoint: document.querySelector('#checkpoint').value.trim(),
            relays: document.querySelector('#relays').value.split(',').map(value => value.trim()),
        });
        globalThis.nethermind = provider;
        provider.on('message', message => { status.textContent = message.data; });
        provider.on('disconnect', error => { status.textContent = error.message; });
        status.textContent = `Consensus verified. Chain ${await provider.request({ method: 'eth_chainId' })}.`;
        document.querySelector('#balance').classList.remove('hidden');
    } catch (error) { status.textContent = error.message; }
    finally { button.disabled = false; }
});

document.querySelector('#balance').addEventListener('submit', async event => {
    event.preventDefault();
    const button = event.currentTarget.querySelector('button');
    button.disabled = true;
    const started = performance.now();
    try {
        const balance = await provider.request({ method: 'eth_getBalance', params: [document.querySelector('#address').value.trim(), 'finalized'] });
        const wei = BigInt(balance);
        result.textContent = `${wei} wei\n${wei / 10n ** 18n}.${(wei % 10n ** 18n).toString().padStart(18, '0')} ETH\nVerified in ${(performance.now() - started).toFixed(1)} ms`;
    } catch (error) { result.textContent = error.message; }
    finally { result.classList.remove('hidden'); button.disabled = false; }
});
