#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["YoutubeStreamRecord/YoutubeStreamRecord.csproj", "YoutubeStreamRecord/"]
RUN dotnet restore "YoutubeStreamRecord/YoutubeStreamRecord.csproj"
COPY . .
WORKDIR "/src/YoutubeStreamRecord"
RUN dotnet build "YoutubeStreamRecord.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "YoutubeStreamRecord.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM jun112561/dotnet_with_yt-dlp:2023.12.30 AS base
WORKDIR /app
COPY --from=publish /app/publish .

ENV TZ="Asia/Taipei"

STOPSIGNAL SIGQUIT

ENTRYPOINT ["dotnet", "YoutubeStreamRecord.dll"]
CMD [ "sub", "-d", "-s", "-o", "/output", "-t", "/temp_path", "-u", "/unarchived_stream", "-m", "/member_only_stream"]