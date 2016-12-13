#!/usr/bin/env bash

dotnet restore ../../../
dotnet publish ../../../samples/SampleApp/
cp -R ../../../samples/SampleApp/bin/Debug/netcoreapp1.1/publish/ .
image=$(docker build -qf Dockerfile .)
container=$(docker run -Ptd --privileged $image)

# Try to connect to SampleApp once a second up to 10 times.
for i in {1..10}; do curl $(docker port $container 8080/tcp) && exit 0 || sleep 1; done

exit -1
