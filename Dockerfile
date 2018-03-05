from dcurylo/paket
# from microsoft/dotnet:2-sdk

run curl -sL https://deb.nodesource.com/setup_6.x | bash -

run apt-get update && apt-get -y install build-essential libssl-dev nodejs

run npm i yarn npm5 -g

copy . /root/app

workdir /root/app

run yarn install

workdir /root/app/src/Client

run dotnet restore
run dotnet fable webpack -p

workdir /root/app/src/Server

run dotnet restore

entrypoint bash
