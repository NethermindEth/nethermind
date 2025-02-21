// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

/**
 * Formats a duration (in milliseconds) as d h m s, or h m s, etc.
 */
export function formatDuration(ms: number): string {
  function pad(num: number): string {
    return num.toString().padStart(2, '0');
  }

  let totalSeconds = Math.floor(ms / 1000);
  let totalMinutes = Math.floor(totalSeconds / 60);
  let totalHours = Math.floor(totalMinutes / 60);

  let days = Math.floor(totalHours / 24);
  let hours = totalHours % 24;
  let minutes = totalMinutes % 60;
  let seconds = totalSeconds % 60;

  if (days === 0 && hours === 0 && minutes === 0 && seconds === 0) {
    return '0s';
  }

  if (days > 0) {
    return `${days}d ${pad(hours)}h ${pad(minutes)}m ${pad(seconds)}s`;
  }

  if (hours > 0) {
    return `${hours}h ${pad(minutes)}m ${pad(seconds)}s`;
  }

  if (minutes > 0) {
    return `${minutes}m ${pad(seconds)}s`;
  }

  return `${seconds}s`;
}
