// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

export function updateText(element: HTMLElement, value: string): void {
  if (element.innerText !== value) {
    // Don't update the DOM if the value is the same
    element.innerText = value;
  }
}
