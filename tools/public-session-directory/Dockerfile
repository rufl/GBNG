FROM python:3.13-alpine@sha256:1e1f6334529d1fb26a2ef619d7cffb30a7c7d7cc1cc7338ebee283a58d1aeaca

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app
COPY --chown=65532:65532 server.py /app/server.py
USER 65532:65532
EXPOSE 47800
ENTRYPOINT ["python", "/app/server.py"]
CMD ["--bind", "0.0.0.0", "--port", "47800"]
