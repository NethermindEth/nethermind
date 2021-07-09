On_Green="\033[42m"
Color_Off="\033[0m"

echo -e "${On_Green} Shutting down nethermind.service... ${Color_Off}"

stopNethermind() {
	sudo systemctl stop nethermind
}

stopNethermind

echo -e "${On_Green} OK ${Color_Off}"