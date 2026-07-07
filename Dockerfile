# ── ensemble-runner: reproducible submission container ───────────────────────
# Multi-stage: SDK build -> slim runtime. The runner talks to model servers over
# HTTP (OpenAI-compatible), so the container carries NO model weights and NO GPU
# deps itself — it points at whatever endpoint the environment exposes:
#   local voters: --base-url / BASE_URL   (LM Studio, vLLM on ROCm, llama.cpp, ...)
#   remote:       FIREWORKS_API_KEY env   (never baked into the image)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# PUBLIC IMAGE: builds act2-harness.csproj — the contract subset ONLY.
# The measurement toolkit (act2.csproj) is never copied into this image.
COPY act2-harness.csproj .
RUN dotnet restore act2-harness.csproj
COPY src/Harness.cs src/HarnessProgram.cs src/Fireworks.cs src/Backend.cs src/Records.cs ./src/
RUN dotnet publish act2-harness.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# data mounts at /data; prompts/captures live there, not in the image
WORKDIR /data
ENTRYPOINT ["dotnet", "/app/act2-agent.dll"]
# graded default: the Track 1 contract. Local dev: docker compose run runner <subcommand>
CMD ["harness"]
