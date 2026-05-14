FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/BackgroundWorker/BackgroundWorker.csproj", "src/BackgroundWorker/"]
COPY ["src/Shared/Shared.csproj", "src/Shared/"]
RUN dotnet restore "src/BackgroundWorker/BackgroundWorker.csproj"
COPY . .
RUN dotnet build "src/BackgroundWorker/BackgroundWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/BackgroundWorker/BackgroundWorker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BackgroundWorker.dll"]
