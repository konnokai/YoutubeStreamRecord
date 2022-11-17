#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Youtube Stream Record/Youtube Stream Record.csproj", "Youtube Stream Record/"]
RUN dotnet restore "Youtube Stream Record/Youtube Stream Record.csproj"
COPY . .
WORKDIR "/src/Youtube Stream Record"
RUN dotnet build "Youtube Stream Record.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Youtube Stream Record.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM jun112561/dotnet_with_yt-dlp:2022.11.11 AS base
WORKDIR /app
COPY --from=publish /app/publish .

ENV TZ="Asia/Taipei"

STOPSIGNAL SIGQUIT

ENTRYPOINT ["dotnet", "Youtube Stream Record.dll", "sub", "-d", "-s", "-o", "/output", "-t", "/temp_path", "-u", "/unarchived_stream"]