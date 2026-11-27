# Шлюз ReportPalette: FastAPI, без ядра ManagerDataBase внутри.
# Ядро теперь отдельный модуль-контейнер (см. modules.json и docker-compose.yml).
#
# Сборка (из корня репозитория):  docker build -t reportpalette-backend ./backend

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py .
COPY modules.json .

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD python -c "import urllib.request;urllib.request.urlopen('http://127.0.0.1:8000/health')" || exit 1

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
