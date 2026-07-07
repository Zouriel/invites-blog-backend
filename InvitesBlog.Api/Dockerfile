# Multi-stage build for the InvitesBlog API (.NET 10).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY InvitesBlog.Domain/ InvitesBlog.Domain/
COPY InvitesBlog.Application/ InvitesBlog.Application/
COPY InvitesBlog.Infrastructure/ InvitesBlog.Infrastructure/
COPY InvitesBlog.TemplateCompiler/ InvitesBlog.TemplateCompiler/
COPY InvitesBlog.Api/ InvitesBlog.Api/
RUN dotnet restore InvitesBlog.Api/InvitesBlog.Api.csproj
RUN dotnet publish InvitesBlog.Api/InvitesBlog.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "InvitesBlog.Api.dll"]
