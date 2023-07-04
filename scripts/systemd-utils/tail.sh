# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

LOG_PATH="data/logs/*.logs.txt"

tailLogs() {
	tail -f $HOME/$LOG_PATH
}

tailLogs
