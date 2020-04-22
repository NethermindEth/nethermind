#!/bin/bash
echo "====================================="
echo "Launching Release Process"
echo "====================================="
START=`date +%s`
DOCKER_IMAGE="nethermind/nethermind"
WEBHOOK_URL="{{ Place for slack webhook }}"

if [ "$1" != "" ]; then
    echo "Running build from $1 branch..."
    cd ~/repo_pub/
    # STAGE 1
    ./build-packages.sh $1
    send_message_to_slack "1" "Nethermind packages have been built"

    # STAGE 2
    ./publish-packages.sh
    send_message_to_slack "2" "Nethermind packages have been published to GitHub"
    
    # STAGE 3
    ./publish-downloads.sh
    send_message_to_slack "3" "Nethermind packages have been published to Downloads"

    # STAGE 4
    # Building & pushing docker images
    cd ~
    ./dockers.sh
    send_message_to_slack "4" "Docker builds have been initiated on GitHub Actions"

    # STAGE 5
    # Finishing Nethermind repo part
    send_message_to_slack "5" "Nethermind build process has been finished. Moving to NDM part"

    cd ~/repo_ndm/
    # STAGE 6
    ./build-packages.sh $1
    send_message_to_slack "6" "NDM packages have been built"

    # STAGE 7
    ./publish-downloads.sh
    send_message_to_slack "7" "NDM packages have been published to Downloads"

    # STAGE 8
    # Finishing NDM repo part
    send_message_to_slack "8" "Release build process has been finished"

    cd ~/repo_pub/nethermind
    GIT_SSH_COMMAND='ssh -i ~/.ssh/id_rsa_nethermind' git checkout master
    cd ~/repo_ndm/ndm/src/nethermind
    GIT_SSH_COMMAND='ssh -i ~/.ssh/id_rsa_nethermind_ndm' git checkout master
else
    echo "Running build from master branch..."
    cd ~/repo_pub/
     # STAGE 1
    ./build-packages.sh
    send_message_to_slack "1" "Nethermind packages have been built"

    # STAGE 2
    ./publish-packages.sh
    send_message_to_slack "2" "Nethermind packages have been published to GitHub"
    
    # STAGE 3
    ./publish-downloads.sh
    send_message_to_slack "3" "Nethermind packages have been published to Downloads"

    # STAGE 4
    # Building & pushing docker images
    cd ~
    ./dockers.sh
    send_message_to_slack "4" "Docker builds have been initiated on GitHub Actions"

    # STAGE 5
    # Finishing Nethermind repo part
    send_message_to_slack "5" "Nethermind build process has been finished. Moving to NDM part"

    # Trigger dappnode build
    curl -v -X POST -u "{{ Place for GitHub Token }}" -H "Accept: application/vnd.github.everest-preview+json" -H "Content-Type: application/json" --data '{"event_type":"dappnode"}' https://api.github.com/repos/nethermindeth/nethermind/dispatches
    
    cd ~/repo_ndm/
    # STAGE 6
    ./build-packages.sh $1
    send_message_to_slack "6" "NDM packages have been built"

    # STAGE 7
    ./publish-downloads.sh
    send_message_to_slack "7" "NDM packages have been published to Downloads"

    # STAGE 8
    # Finishing NDM repo part
    send_message_to_slack "8" "Release build process has been finished"
fi

echo "====================================="
echo "Release Process has been finished"
echo "====================================="

function send_message_to_slack () {
    END=`date +%s`
    RUNTIME=`date -d@$((END-START)) -u +%H:%M:%S`
    STAGE=$(printf '{"blocks": [{"type": "section","text": {"type": "mrkdwn", "text": ":heavy_check_mark: `Stage '$1'/8. '$2'.`"}},{"type": "context","elements": [{"type": "mrkdwn","text": "Total time: %s"}]}]}' $RUNTIME)
    curl --data "$STAGE" $WEBHOOK_URL
}