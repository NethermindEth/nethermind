#!/bin/bash
echo "====================================="
echo "Launching Release Process"
echo "====================================="
START=`date +%s`
DOCKER_IMAGE="nethermind/nethermind"
WEBHOOK_URL="{{ Place for Slack WebHook }}"

function send_message_to_slack () {
    END=`date +%s`
    RUNTIME=`date -d@$((END-START)) -u +%H:%M:%S`
    STAGE=$(printf '{"blocks": [{"type": "section","text": {"type": "mrkdwn", "text": ":heavy_check_mark: `Stage %s/8. %s.`"}},{"type": "context","elements": [{"type": "mrkdwn","text": "Total time: %s"}]}]}' $1 "$2" $RUNTIME)
    curl --data "$STAGE" $WEBHOOK_URL
}

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
    ./dockers.sh $1
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
    
    cd ~/repo_ndm/
    # STAGE 6
    ./build-packages.sh
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