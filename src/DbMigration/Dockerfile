FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder
WORKDIR /src
COPY ["src/DbMigration/DbMigration.csproj", "src/DbMigration/"]
COPY ["src/Shared/Shared.csproj", "src/Shared/"]
COPY ["src/ApiGateway/ApiGateway.csproj", "src/ApiGateway/"]
RUN dotnet restore "src/DbMigration/DbMigration.csproj"
COPY . .
RUN dotnet build "src/DbMigration/DbMigration.csproj" -c Release -o /app/build

FROM builder AS publish
RUN dotnet publish "src/DbMigration/DbMigration.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DbMigration.dll"]
