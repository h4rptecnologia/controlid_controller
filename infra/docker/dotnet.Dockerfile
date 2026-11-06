# Uso: docker build -f infra/docker/dotnet.Dockerfile --build-arg PROJECT=Platform.Api --build-arg BASE=aspnet .
ARG BASE=aspnet
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
ARG PROJECT
WORKDIR /repo
COPY . .
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj" --locked-mode \
 && dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c AS base-aspnet
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:4f1f30cf0bb4ab0c8b2daa84613051683456f8df4a9bf7c812e20d74962992ac AS base-runtime

FROM base-${BASE}
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /out .
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\" \"$@\"", "--"]
