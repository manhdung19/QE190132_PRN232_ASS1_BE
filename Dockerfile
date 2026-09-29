# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for efficient Docker layer caching
COPY ["QE190132_PRN232_Ass1_BE.sln", "./"]
COPY ["TaskTrack.API/TaskTrack.API.csproj", "TaskTrack.API/"]
COPY ["TaskTrack.Repo/TaskTrack.Repo.csproj", "TaskTrack.Repo/"]
COPY ["TaskTrack.Service/TaskTrack.Service.csproj", "TaskTrack.Service/"]

RUN dotnet restore "TaskTrack.API/TaskTrack.API.csproj"

# Copy source code
COPY TaskTrack.API/ TaskTrack.API/
COPY TaskTrack.Repo/ TaskTrack.Repo/
COPY TaskTrack.Service/ TaskTrack.Service/

WORKDIR "/src/TaskTrack.API"
RUN dotnet publish "TaskTrack.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TaskTrack.API.dll"]
