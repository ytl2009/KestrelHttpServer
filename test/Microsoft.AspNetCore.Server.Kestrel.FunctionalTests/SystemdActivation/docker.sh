#!/usr/bin/env bash

# Ensure that dotnet is added to the PATH
scriptDir=$(dirname "${BASH_SOURCE[0]}")
repoDir=$(cd $scriptDir/../../.. && pwd)
source ./.build/KoreBuild.sh -r $repoDir --quiet

dotnet restore
dotnet publish ./samples/SampleApp/
cp -R ./samples/SampleApp/bin/Debug/netcoreapp1.1/publish/ $scriptDir

image=$(docker build -qf $scriptDir/Dockerfile $scriptDir)
container=$(docker run -Ptd --privileged $image)

# Try to connect to SampleApp once a second up to 10 times.
for i in {1..10}; do curl $(docker port $container 8080/tcp) && exit 0 || sleep 1; done

exit -1
