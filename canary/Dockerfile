# RouteZero Canary — reference forwarder
# Build:  docker buildx build --platform linux/amd64 -t <registry>/routezero-canary:latest --push .
FROM python:3.11-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY main.py .

CMD ["python", "main.py"]
