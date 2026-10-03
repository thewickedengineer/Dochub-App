# Python 3.12, as the spec pins; the code also runs on 3.14 for local work.
FROM python:3.12-slim

# libmagic for content sniffing (puremagic is the fallback when it is absent);
# libgl1 and libglib2.0-0 for the OpenCV that Docling's OCR uses; ffmpeg for audio/video.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libmagic1 libgl1 libglib2.0-0 ffmpeg \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY pyproject.toml ./
COPY src ./src
COPY migrations ./migrations
# CPU-only torch first: the default wheel bundles CUDA and is several GB larger.
RUN pip install --no-cache-dir torch torchvision --index-url https://download.pytorch.org/whl/cpu \
 && pip install --no-cache-dir -e ".[local,magic,docs,media]"

# Embedding, Docling and Whisper models download here on first use; keep it a volume.
ENV HF_HOME=/models FASTEMBED_CACHE_PATH=/models PYTHONUNBUFFERED=1 LOG_JSON=true
VOLUME ["/models"]

EXPOSE 8090
ENTRYPOINT []
CMD ["rag-ingest", "worker"]
