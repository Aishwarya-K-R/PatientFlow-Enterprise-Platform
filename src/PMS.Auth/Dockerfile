# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["PatientFlow.sln", "./"]
COPY ["src/PMS.Contracts/PMS.Contracts.csproj", "src/PMS.Contracts/"]
COPY ["src/PMS.Common/PMS.Common.csproj", "src/PMS.Common/"]
COPY ["src/PMS.Auth/PMS.Auth.csproj", "src/PMS.Auth/"]

RUN dotnet restore "src/PMS.Auth/PMS.Auth.csproj"

COPY . .

WORKDIR "/src/src/PMS.Auth"
RUN dotnet build "PMS.Auth.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PMS.Auth.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PMS.Auth.dll"]
