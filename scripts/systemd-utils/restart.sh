# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

On_Green="\033[42m"
Color_Off="\033[0m"

echo -e "${On_Green} Restarting the nethermind.service... ${Color_Off}"

restartNethermind() {
	sudo systemctl restart nethermind
}

restartNethermind

echo -e "${On_Green} OK ${Color_Off}"
