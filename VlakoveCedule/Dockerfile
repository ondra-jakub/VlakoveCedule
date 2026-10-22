FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["VlakoveCedule.Base/VlakoveCedule.Base.csproj", "VlakoveCedule.Base/"]
COPY ["VlakoveCedule.Contracts/VlakoveCedule.Contracts.csproj", "VlakoveCedule.Contracts/"]
COPY ["VlakoveCedule.Core/VlakoveCedule.Core.csproj", "VlakoveCedule.Core/"]
COPY ["VlakoveCedule/VlakoveCedule.csproj", "VlakoveCedule/"]
RUN dotnet restore "VlakoveCedule/VlakoveCedule.csproj"
COPY . .
WORKDIR "/src/VlakoveCedule"
RUN dotnet build "./VlakoveCedule.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./VlakoveCedule.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "VlakoveCedule.dll"]
