# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["COMS MVC.csproj", "./"]
RUN dotnet restore "COMS MVC.csproj"
COPY . .
RUN dotnet publish "COMS MVC.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# Railway injects PORT at runtime; Program.cs binds http://0.0.0.0:$PORT.
ENTRYPOINT ["dotnet", "COMS MVC.dll"]
