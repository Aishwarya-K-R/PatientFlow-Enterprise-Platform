# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["PatientFlow.sln", "./"]
COPY ["src/PMS.Contracts/PMS.Contracts.csproj", "src/PMS.Contracts/"]
COPY ["src/PMS.Common/PMS.Common.csproj", "src/PMS.Common/"]
COPY ["src/PMS.Patient/PMS.Patient.csproj", "src/PMS.Patient/"]

RUN dotnet restore "src/PMS.Patient/PMS.Patient.csproj"

COPY . .

WORKDIR "/src/src/PMS.Patient"
RUN dotnet build "PMS.Patient.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PMS.Patient.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PMS.Patient.dll"]
