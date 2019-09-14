#!/bin/bash
GIT_TAG=$(git describe --tags --always | cut -d- -f1)
DOCKER_TAGS="latest $GIT_TAG"
DOCKER_IMAGE_NAME=nethermind

if [ "$TRAVIS_BRANCH" != "master" ]; then
    exit 0
fi

if [[ !$GIT_TAG =~ ^v\d* ]] || [[ !$GIT_TAG =~ ^stable_* ]] || [[ !$GIT_TAG =~ ^1.0rc\d* ]]; then
    exit 0
fi

docker login -u $DOCKER_USERNAME -p $DOCKER_PASSWORD

for DOCKER_TAG in $DOCKER_TAGS; do
    docker build -t $DOCKER_IMAGE_NAME:$DOCKER_TAG --build-arg GIT_COMMIT=$(git log -1 --format=%h) .
    docker tag $DOCKER_IMAGE_NAME:$DOCKER_TAG $DOCKER_USERNAME/$DOCKER_IMAGE_NAME:$DOCKER_TAG
    docker push $DOCKER_USERNAME/$DOCKER_IMAGE_NAME:$DOCKER_TAG
done
