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

# https://ffbinaries.com/downloads
ENV FFMPEG_VER="4.4.1"

RUN set -xe; \
    apt-get update; \
    apt-get install -y --no-install-recommends wget unzip; \
    wget https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux; \
    wget https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v$FFMPEG_VER/ffmpeg-$FFMPEG_VER-linux-64.zip; \
	wget https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v$FFMPEG_VER/ffprobe-$FFMPEG_VER-linux-64.zip; \
    unzip ffmpeg-$FFMPEG_VER-linux-64.zip; \
    unzip ffprobe-$FFMPEG_VER-linux-64.zip; \
    rm ffmpeg-$FFMPEG_VER-linux-64.zip; \
    rm ffprobe-$FFMPEG_VER-linux-64.zip; \
    chmod +x yt-dlp_linux; \
    chmod +x ffmpeg; \
    chmod +x ffprobe; \
    mv yt-dlp_linux /usr/local/bin/yt-dlp; \
    mv ffmpeg /usr/local/bin/ffmpeg; \
    mv ffprobe /usr/local/bin/ffprobe; \
    apt-get purge -y wget unzip; \
    apt-get autoremove -y; \
    apt-get autoclean -y

ENV TZ="Asia/Taipei"

CMD dotnet "Youtube Stream Record.dll" sub -d -s -o /output -t /temp_path -u /unarchived_stream