On_Green="\033[42m"
Color_Off="\033[0m"

echo -e "${On_Green} Status ${Color_Off}"

displayStatus() {
	sudo systemctl status nethermind
}

displayStatus