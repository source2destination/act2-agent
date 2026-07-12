#!/bin/sh
# R-ZERO entrypoint v2 — NON-BLOCKING startup.
# Launch llama-server in the background, wait up to 45s for health, then exec
# the harness REGARDLESS. Never blocks past the bound, never exits on a slow
# model load. Hybrid mode makes this safe: local calls before the server is
# healthy miss and fall through to the remote ladder; once the model loads,
# the local lane takes over mid-run.
/opt/llama/llama-server \
  --model /models/model.gguf \
  --ctx-size 4096 --parallel 2 --threads 2 \
  --host 127.0.0.1 --port 8080 --log-disable &
i=0
while [ $i -lt 45 ]; do
  if wget -q -O /dev/null http://127.0.0.1:8080/health 2>/dev/null; then
    echo "llama-server healthy after ${i}s" >&2
    break
  fi
  i=$((i+1)); sleep 1
done
[ $i -ge 45 ] && echo "llama-server not healthy at 45s — proceeding, local lane will retry" >&2
exec dotnet /app/act2-agent.dll "$@"
