// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import { Processed } from './types';
import { updateText } from './utilities';
import { format } from './format';

export class GasInfo {
  private lastGasLimit = 36_000_000;
  private minGas: HTMLElement;
  private medianGas: HTMLElement;
  private aveGas: HTMLElement;
  private maxGas: HTMLElement;
  private gasLimit: HTMLElement;
  private gasLimitDelta: HTMLElement;

  constructor(minGas: string, medianGas: string, aveGas: string, maxGas: string, gasLimit: string, gasLimitDelta: string) {
    this.minGas = document.getElementById(minGas);
    this.medianGas = document.getElementById(medianGas);
    this.aveGas = document.getElementById(aveGas);
    this.maxGas = document.getElementById(maxGas);
    this.gasLimit = document.getElementById(gasLimit);
    this.gasLimitDelta = document.getElementById(gasLimitDelta);
  }

  parseEvent(e) {
    if (document.hidden) return;
    const data = JSON.parse(e.data) as Processed;

    updateText(this.minGas, data.minGas.toFixed(2));
    updateText(this.medianGas, data.medianGas.toFixed(2));
    updateText(this.aveGas, data.aveGas.toFixed(2));
    updateText(this.maxGas, data.maxGas.toFixed(2));
    updateText(this.gasLimit, format(data.gasLimit));
    updateText(this.gasLimitDelta, data.gasLimit > this.lastGasLimit ? '👆' : data.gasLimit < this.lastGasLimit ? '👇' : '👈');

    this.lastGasLimit = data.gasLimit;
  }
}
