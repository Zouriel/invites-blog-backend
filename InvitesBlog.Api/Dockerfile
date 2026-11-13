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

# GPU libraries for face grouping (FaceAnalyzer): ONNX Runtime 1.26's CUDA 12 build with cuDNN 9.8, the
# versions proven on the server's GTX 1050 Ti (Pascal). Newer cuDNN releases have no kernels for that card
# and CUDA 13 dropped it, so these stay pinned. Only the shared libraries are kept. The driver (libcuda) comes
# from the host through the container's GPU device (CDI). Build with --build-arg GPU_LIBS=off to leave them
# out: faces then run on the CPU.
FROM python:3.12-slim AS gpulibs
ARG GPU_LIBS=on
RUN mkdir -p /opt/gpu-libs && if [ "$GPU_LIBS" = "on" ]; then \
      pip install --no-cache-dir --root-user-action=ignore \
        nvidia-cudnn-cu12==9.8.0.87 nvidia-cublas-cu12==12.9.3.13 nvidia-cuda-runtime-cu12==12.9.79 \
        nvidia-cufft-cu12==11.4.1.4 nvidia-curand-cu12==10.3.10.19 nvidia-cuda-nvrtc-cu12==12.9.86 \
        nvidia-nvjitlink-cu12==12.9.86 \
      && cp -a /usr/local/lib/python3.12/site-packages/nvidia/*/lib/*.so* /opt/gpu-libs/ ; fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# For videos: checking what arrived, removing camera metadata, trimming (VideoTrimService).
# heif-dec (libheif-examples + its HEVC decoder plugin): iPhone HEIC pictures uploaded into a portfolio (HeifDecConverter).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg tzdata libheif-examples libheif-plugin-libde265 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=gpulibs /opt/gpu-libs /opt/gpu-libs
ENV LD_LIBRARY_PATH=/opt/gpu-libs
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "InvitesBlog.Api.dll"]
