# Бэкенд ReportPalette: FastAPI + ядро ManagerDataBase.
#
# Перед сборкой клонируйте ядро рядом с main.py:
#   git clone https://github.com/volnaogt-organization/ManagerDataBase backend/ManagerDataBase
#
# Сборка (из корня репозитория):  docker build -t reportpalette-backend ./backend

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

# Сначала зависимости — для кэширования слоёв
COPY requirements.txt .

# pyodbc собирается из исходников: нужны gcc + unixodbc-dev.
# msodbcsql18 — драйвер SQL Server для подключений ManagerDataBase.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg gcc g++ unixodbc-dev \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
      | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
 && curl -fsSL https://packages.microsoft.com/config/debian/12/prod.list \
      -o /etc/apt/sources.list.d/mssql-release.list \
 && apt-get update \
 && ACCEPT_EULA=Y apt-get install -y --no-install-recommends msodbcsql18 \
 && pip install --no-cache-dir -r requirements.txt \
 && apt-get purge -y gcc g++ \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# Код сервиса и ядро ManagerDataBase (клонируется заранее, см. выше)
COPY main.py .
COPY ManagerDataBase ./ManagerDataBase

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD python -c "import urllib.request;urllib.request.urlopen('http://127.0.0.1:8000/health')" || exit 1

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
