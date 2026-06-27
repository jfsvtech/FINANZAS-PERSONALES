FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY FinanzasPersonales.sln ./
COPY src/FinanzasPersonales.Web/FinanzasPersonales.Web.csproj src/FinanzasPersonales.Web/
RUN dotnet restore src/FinanzasPersonales.Web/FinanzasPersonales.Web.csproj

COPY . .
RUN dotnet publish src/FinanzasPersonales.Web/FinanzasPersonales.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./
COPY docs /docs
COPY output/pdf /output/pdf

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["sh", "-c", "dotnet FinanzasPersonales.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
