FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies before copying source so dependency layers remain cacheable.
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.config BannedSymbols.txt ./
COPY build ./build
COPY tools/olive/prune_attention_outputs.py ./tools/olive/prune_attention_outputs.py
COPY runtime/trt-rtx-ep.manifest.json ./runtime/trt-rtx-ep.manifest.json
COPY src/Trackdub.Cli/Trackdub.Cli.csproj src/Trackdub.Cli/
COPY src/Trackdub.Sdk/Trackdub.Sdk.csproj src/Trackdub.Sdk/
COPY src/Trackdub.Application/Trackdub.Application.csproj src/Trackdub.Application/
COPY src/Trackdub.Contracts/Trackdub.Contracts.csproj src/Trackdub.Contracts/
COPY src/Trackdub.Domain/Trackdub.Domain.csproj src/Trackdub.Domain/
COPY src/Trackdub.Composition/Trackdub.Composition.csproj src/Trackdub.Composition/
COPY src/Trackdub.Infrastructure/Trackdub.Infrastructure.csproj src/Trackdub.Infrastructure/
COPY src/Trackdub.Inference/Trackdub.Inference.csproj src/Trackdub.Inference/
COPY src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj src/Trackdub.Inference.Onnx/
COPY src/Trackdub.Licensing/Trackdub.Licensing.csproj src/Trackdub.Licensing/
COPY src/Trackdub.Media/Trackdub.Media.csproj src/Trackdub.Media/
COPY src/Trackdub.Media.Playback/Trackdub.Media.Playback.csproj src/Trackdub.Media.Playback/
COPY src/Trackdub.Analyzers/Trackdub.Analyzers.csproj src/Trackdub.Analyzers/
COPY src/Trackdub.OnnxRuntime.Dnnl.Native/Trackdub.OnnxRuntime.Dnnl.Native.csproj src/Trackdub.OnnxRuntime.Dnnl.Native/

RUN dotnet restore src/Trackdub.Cli/Trackdub.Cli.csproj -p:TargetFramework=net10.0 -r linux-x64
RUN dotnet restore src/Trackdub.Analyzers/Trackdub.Analyzers.csproj -p:TargetFramework=netstandard2.0

COPY src ./src
RUN dotnet publish src/Trackdub.Cli/Trackdub.Cli.csproj \
    --configuration Release \
    --framework net10.0 \
    --runtime linux-x64 \
    --self-contained false \
    --no-restore \
    --output /app/publish \
    /p:PublishTrimmed=false \
    /p:PublishReadyToRun=false \
    /p:DebugType=None \
    /p:DebugSymbols=false

FROM nvidia/cuda:13.0.0-cudnn-runtime-ubuntu24.04 AS cuda-runtime

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install --no-install-recommends --yes ca-certificates espeak-ng ffmpeg libgomp1 libstdc++6 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir --parents /usr/local/cuda/lib \
    && groupadd --system --gid 10001 trackdub \
    && useradd --system --uid 10001 --gid trackdub --create-home --home-dir /home/trackdub trackdub \
    && mkdir --parents /data/models /data/projects /work/input /work/output /work/tmp /logs \
    && chown --recursive trackdub:trackdub /app /data /work /logs /home/trackdub

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_TieredCompilation=1 \
    DOTNET_TieredCompilationQuickJit=1 \
    DATABASE_PATH=/data/trackdub.db \
    MODELS_CACHE_DIR=/data/models \
    AUTO_DOWNLOAD_MODELS=false \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility \
    ONNX_PROVIDER=cuda \
    TMPDIR=/work/tmp

COPY --from=cuda-runtime /usr/local/cuda-13.0/targets/x86_64-linux/lib/ /usr/local/cuda/lib/
COPY --from=cuda-runtime /usr/lib/x86_64-linux-gnu/libcudnn*.so* /usr/local/cuda/lib/
RUN printf '/usr/local/cuda/lib\n' > /etc/ld.so.conf.d/cuda.conf && ldconfig

COPY --from=build --chown=trackdub:trackdub /app/publish ./
COPY --from=build --chown=trackdub:trackdub /src/src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json ./src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json

USER trackdub
ENTRYPOINT ["dotnet", "Trackdub.Cli.dll"]
CMD ["doctor"]
