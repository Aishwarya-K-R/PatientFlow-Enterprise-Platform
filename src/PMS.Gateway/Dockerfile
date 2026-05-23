# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["PatientFlow.sln", "./"]
COPY ["src/PMS.Contracts/PMS.Contracts.csproj", "src/PMS.Contracts/"]
COPY ["src/PMS.Common/PMS.Common.csproj", "src/PMS.Common/"]
COPY ["src/PMS.Gateway/PMS.Gateway.csproj", "src/PMS.Gateway/"]

# Restore dependencies
RUN dotnet restore "src/PMS.Gateway/PMS.Gateway.csproj"

# Copy all source code
COPY . .

# Build
WORKDIR "/src/src/PMS.Gateway"
RUN dotnet build "PMS.Gateway.csproj" -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish "PMS.Gateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PMS.Gateway.dll"]
