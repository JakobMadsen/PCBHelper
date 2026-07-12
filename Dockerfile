# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS source
WORKDIR /src
COPY . .
RUN dotnet restore PCBHelper.slnx

FROM source AS core-test
RUN dotnet build PCBHelper.slnx --configuration Release --no-restore
RUN dotnet test tests/PCBHelper.Core.Tests/PCBHelper.Core.Tests.csproj --configuration Release --no-build --logger "console;verbosity=minimal"
RUN dotnet test tests/PCBHelper.Contract.Tests/PCBHelper.Contract.Tests.csproj --configuration Release --no-build --logger "console;verbosity=minimal"

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS eda-base
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends software-properties-common ca-certificates gnupg ngspice openjdk-21-jre-headless \
    && add-apt-repository --yes ppa:kicad/kicad-10.0-releases \
    && apt-get update \
    && apt-get install -y --no-install-recommends kicad \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet restore PCBHelper.slnx

FROM eda-base AS eda-test
ENV KICAD_CLI=/usr/bin/kicad-cli
ENV NGSPICE=/usr/bin/ngspice
RUN dotnet build PCBHelper.slnx --configuration Release --no-restore
RUN dotnet run --no-build --configuration Release --project src/PCBHelper.Cli -- doctor --json \
    && dotnet run --no-build --configuration Release --project src/PCBHelper.Cli -- simulation status --json
RUN dotnet test tests/PCBHelper.E2E.Tests/PCBHelper.E2E.Tests.csproj --configuration Release --no-build --logger "console;verbosity=minimal"
