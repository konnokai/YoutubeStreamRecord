#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["StreamRecordTools/StreamRecordTools.csproj", "StreamRecordTools/"]
RUN dotnet restore "StreamRecordTools/StreamRecordTools.csproj"
COPY . .
WORKDIR "/src/StreamRecordTools"
RUN dotnet build "StreamRecordTools.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "StreamRecordTools.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM jun112561/dotnet_with_yt-dlp:2024.10.07 AS base
WORKDIR /app
COPY --from=publish /app/publish .

ENV TZ="Asia/Taipei"

STOPSIGNAL SIGQUIT

ENTRYPOINT ["dotnet", "StreamRecordTools.dll"]
CMD [ "sub", "--audo-delete", "-s", "-o", "/output", "-t", "/temp_path", "-u", "/unarchived_stream", "-m", "/member_only_stream"]