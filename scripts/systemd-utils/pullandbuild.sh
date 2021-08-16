On_Green="\033[42m"
Color_Off="\033[0m"
BRANCH=$1
BUILD_DIR="build"
BUILD_NEW_DIR="build_new"
NETHERMIND_DIR="nethermind"

gitPullOrigin() {
	echo -e "${On_Green} Pulling origin master from Github... ${Color_Off}"
	git pull origin master
}

gitCheckout() {
	if [[ ! -z "$BRANCH" ]]
	then
		echo -e "${On_Green} Checking out branch/tag: ${BRANCH} ${Color_Off}"
		git checkout $BRANCH
	fi
}

gitPull() {
        echo -e "${On_Green} Pulling latest changes from Github... ${Color_Off}"
        git pull
}

gitSubmoduleUpdate() {
	echo -e "${On_Green} Updating submodules... ${Color_Off}"
	git submodule update --init
}

buildNethermind() {
	echo -e "${On_Green} Building Nethermind... ${Color_Off}"
	cd $HOME/nethermind/src/Nethermind/Nethermind.Runner
	dotnet build -c Release -o $HOME/$BUILD_NEW_DIR
}

replaceOldBuild() {
	echo -e "${On_Green} Replacing old build dir... ${Color_Off}"
	rm -rf $HOME/$BUILD_DIR
	mv $HOME/$BUILD_NEW_DIR $BUILD_DIR
}

main() {
	cd $HOME/$NETHERMIND_DIR
	gitPullOrigin
	gitCheckout $BRANCH
	gitPull
	gitSubmoduleUpdate
	buildNethermind
	cd $HOME
	./shutdown.sh
	replaceOldBuild
	./up.sh
}

main