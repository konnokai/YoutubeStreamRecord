#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Youtube Stream Record/Youtube Stream Record.csproj", "Youtube Stream Record/"]
RUN dotnet restore "Youtube Stream Record/Youtube Stream Record.csproj"
COPY . .
WORKDIR "/src/Youtube Stream Record"
RUN dotnet build "Youtube Stream Record.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Youtube Stream Record.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

RUN set -xe; \
    apt-get update; \
    apt-get install -y --no-install-recommends ffmpeg python3 python3-pip ; \
    update-alternatives --install /usr/bin/python python /usr/bin/python3.9 1; \
    pip3 install --no-cache-dir --upgrade yt-dlp; \
    apt-get purge -y python3-pip; \
    chmod +x /usr/local/bin/yt-dlp; \
    apt-get autoremove -y; \
    apt-get autoclean -y

ENV GoogleApiKey=[GoogleApiKey]
ENV RedisOption="127.0.0.1,syncTimeout=3000"

VOLUME [ "/output", "/temp_path", "/unarchived_stream" ]

ENTRYPOINT ["dotnet", "Youtube Stream Record.dll sub -d -s -p /output -t /temp_path -u /unarchived_stream"]