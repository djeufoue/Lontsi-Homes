FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["RentHub.API/RentHub.API.csproj", "RentHub.API/"]
COPY ["Common/Common.csproj", "Common/"]
RUN dotnet restore "RentHub.API/RentHub.API.csproj"

COPY . .
RUN dotnet publish "RentHub.API/RentHub.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "RentHub.API.dll"]
