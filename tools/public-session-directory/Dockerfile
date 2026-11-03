FROM python:3.13-alpine

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app
COPY --chown=65532:65532 server.py /app/server.py
USER 65532:65532
EXPOSE 47800
ENTRYPOINT ["python", "/app/server.py"]
CMD ["--bind", "0.0.0.0", "--port", "47800"]
