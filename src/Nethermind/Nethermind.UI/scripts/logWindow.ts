// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import Convert = require('ansi-to-html');

export class LogWindow {
  private readonly nodeLog: HTMLElement;
  private readonly ansiConvert = new Convert();
  private logs: string[] = [];

  constructor(logElement: string) {
    this.nodeLog = document.getElementById(logElement);
  }

  receivedLog(e) {
    const data = JSON.parse(e.data) as string[];
    for (let entry of data) {
      const html = this.ansiConvert.toHtml(entry);
      if (this.logs.length >= 100) { this.logs.shift(); }
      this.logs.push(html);
    }
  }

  appendLogs() {
    if (this.logs.length > 0) {
      let scroll = false;
      if (this.nodeLog.scrollHeight < 500 || this.nodeLog.scrollTop < this.nodeLog.scrollHeight - 500) {
        scroll = true;
      }
      const frag = document.createDocumentFragment();
      for (let i = 0; i < this.logs.length; i++) {
        const newEntry = document.createElement('div');
        newEntry.innerHTML = this.logs[i];
        frag.appendChild(newEntry);
      }
      this.logs = [];
      this.nodeLog.appendChild(frag);
      let rows = this.nodeLog.childElementCount;
      while (rows > 250) {
        this.nodeLog.firstChild.remove();
        rows--;
      }

      if (scroll) {
        window.setTimeout(() => this.scrollLogs(), 17);
      }
    }
  }

  resize() {
    const bodyRect = document.body.getBoundingClientRect();
    const elemRect = this.nodeLog.getBoundingClientRect();

    var offset = elemRect.top - bodyRect.top;
    var height = window.innerHeight - offset - 16;

    if (height > 0) {
      this.nodeLog.style.height = `${height}px`;
    }
  }

  private scrollLogs() {
    this.nodeLog.scrollTop = this.nodeLog.scrollHeight;
  }
}
