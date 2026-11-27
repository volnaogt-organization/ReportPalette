"""
ReportPalette Gateway — шлюз модульной системы.

Модули (ManagerDataBase и будущие) — отдельные docker-контейнеры со своим API.
Шлюз:
  - хранит реестр модулей (modules.json);
  - проксирует запросы клиентов к модулям;
  - проверяет релизы модулей на GitHub и обновляет контейнеры по кнопке из UI.

Запуск (из папки backend/):
    pip install -r requirements.txt
    uvicorn main:app --host 127.0.0.1 --port 8000

Для функции обновления нужен доступ к Docker-сокету
(в docker-compose монтируется /var/run/docker.sock).
"""

import json
import os
import re
import threading
import time
from typing import Any, Dict, List, Optional

import httpx
from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import Response

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
MODULES_FILE = os.environ.get("MODULES_FILE", os.path.join(BASE_DIR, "modules.json"))
GITHUB_TOKEN = os.environ.get("GITHUB_TOKEN") or None
RELEASE_CACHE_TTL = 600          # сек; защита от rate-limit GitHub API (60/ч без токена)
MODULE_HTTP_TIMEOUT = 1800       # отчёты могут идти долго — как раньше у nginx/BackendClient
HEALTH_TIMEOUT = 3

app = FastAPI(
    title="ReportPalette Gateway",
    description="Шлюз модульной системы ReportPalette: реестр модулей, прокси, автообновление из релизов GitHub",
    version="0.2.0",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


# ---------------------------------------------------------------- реестр ----

_registry_lock = threading.Lock()


def load_registry() -> List[Dict[str, Any]]:
    """Читает modules.json при каждом обращении — файл можно править без пересборки образа."""
    try:
        with open(MODULES_FILE, encoding="utf-8") as f:
            data = json.load(f)
    except FileNotFoundError:
        return []
    except Exception as e:
        raise HTTPException(500, f"Не удалось прочитать {MODULES_FILE}: {e}")
    with _registry_lock:
        return [m for m in data.get("modules", []) if m.get("enabled", True)]


def get_module(name: str) -> Dict[str, Any]:
    for m in load_registry():
        if m.get("name") == name:
            return m
    raise HTTPException(404, f"Модуль '{name}' не найден в реестре")


# ------------------------------------------------------------- версии ----

def parse_version(v: Optional[str]) -> tuple:
    """'v0.5.2' -> (0, 5, 2). Нечисловой хвост отбрасывается."""
    if not v:
        return (0,)
    nums = re.findall(r"\d+", v)
    return tuple(int(x) for x in nums) if nums else (0,)


def version_tuple_str(v: Optional[str]) -> str:
    return v.lstrip("vV") if v else "—"


# ------------------------------------------------------------- GitHub ----

_release_cache: Dict[str, Dict[str, Any]] = {}


def get_latest_release(repo: str) -> Dict[str, Any]:
    """Последний релиз репозитория: {version, url} или {version: None, error}."""
    now = time.time()
    cached = _release_cache.get(repo)
    if cached and now - cached["ts"] < RELEASE_CACHE_TTL:
        return cached["data"]

    headers = {"Accept": "application/vnd.github+json"}
    if GITHUB_TOKEN:
        headers["Authorization"] = f"Bearer {GITHUB_TOKEN}"

    try:
        resp = httpx.get(
            f"https://api.github.com/repos/{repo}/releases/latest",
            headers=headers,
            timeout=10,
        )
        if resp.status_code == 404:
            data = {"version": None, "url": None, "error": None}  # релизов ещё нет
        elif resp.status_code == 200:
            body = resp.json()
            data = {"version": body.get("tag_name"), "url": body.get("html_url"), "error": None}
        else:
            data = {"version": None, "url": None,
                    "error": f"GitHub API: {resp.status_code}"}
    except Exception as e:
        data = {"version": None, "url": None, "error": f"GitHub недоступен: {e}"}

    _release_cache[repo] = {"ts": now, "data": data}
    return data


# ------------------------------------------------------------- Docker ----

_docker_client = None
_docker_lock = threading.Lock()


def docker_client():
    """Ленивый клиент Docker Engine (через смонтированный сокет)."""
    global _docker_client
    with _docker_lock:
        if _docker_client is None:
            import docker  # noqa: WPS433 (ленивый импорт — иначе без сокета не стартует даже /health)
            _docker_client = docker.from_env()
        return _docker_client


def container_version(module: Dict[str, Any]) -> Dict[str, Any]:
    """Версия и статус работающего контейнера модуля.

    Приоритет — ответ /health самого модуля (истина о работающем коде),
    запасной вариант — тег образа контейнера.
    """
    result = {"status": "unknown", "version_running": None}
    try:
        c = docker_client().containers.get(module["container"])
        result["status"] = "up" if c.status == "running" else "down"
        for tag in c.image.tags:
            if tag.startswith(module["image"] + ":"):
                result["version_running"] = tag.rsplit(":", 1)[1]
                break
    except Exception:
        result["status"] = "unknown"

    if result["status"] == "up":
        try:
            r = httpx.get(
                f"http://{module['service']}:{module['port']}/health",
                timeout=HEALTH_TIMEOUT,
            )
            if r.status_code == 200:
                ver = r.json().get("version")
                if ver:
                    result["version_running"] = ver
        except Exception:
            pass
    return result


def wait_healthy(module: Dict[str, Any], timeout: int = 60) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            r = httpx.get(
                f"http://{module['service']}:{module['port']}/health",
                timeout=HEALTH_TIMEOUT,
            )
            if r.status_code == 200:
                return True
        except Exception:
            pass
        time.sleep(2)
    return False


def recreate_container(module: Dict[str, Any], image_ref: str) -> None:
    """Пересоздаёт контейнер модуля на новом образе, сохраняя его конфигурацию."""
    client = docker_client()
    old = client.containers.get(module["container"])
    attrs = old.attrs

    # Переменные окружения из старого контейнера, кроме зашитых в новый образ
    # (MODULE_VERSION должен прийти из нового образа, а не из старого env).
    image_env_keys = {
        kv.split("=", 1)[0]
        for kv in (client.images.get(image_ref).attrs.get("Config", {}).get("Env") or [])
    }
    env = [
        kv for kv in (attrs["Config"].get("Env") or [])
        if kv.split("=", 1)[0] not in image_env_keys
    ]

    host_cfg = attrs.get("HostConfig", {})
    binds = host_cfg.get("Binds") or []
    restart = host_cfg.get("RestartPolicy") or {}
    labels = attrs["Config"].get("Labels") or {}
    networks = list((attrs.get("NetworkSettings", {}).get("Networks") or {}).keys())

    old.stop(timeout=15)
    old.remove()

    # Создаём без сети, затем подключаем сети с алиасом = имя сервиса:
    # так шлюз продолжает резолвить http://<service>:<port> после пересоздания.
    new = client.containers.create(
        image_ref,
        name=module["container"],
        detach=True,
        environment=env,
        volumes=binds,
        labels=labels,
        restart_policy={"Name": restart.get("Name") or "unless-stopped"},
    )
    for net in networks:
        try:
            client.networks.get(net).connect(new, aliases=[module["service"]])
        except Exception:
            pass
    new.start()


# ------------------------------------------------------------- прокси ----

async def proxy_to_module(module: Dict[str, Any], path: str, request: Request) -> Response:
    url = f"http://{module['service']}:{module['port']}/{path.lstrip('/')}"
    body = await request.body()
    headers = {}
    if request.headers.get("content-type"):
        headers["Content-Type"] = request.headers["content-type"]
    try:
        async with httpx.AsyncClient(timeout=MODULE_HTTP_TIMEOUT) as client:
            resp = await client.request(
                request.method,
                url,
                params=dict(request.query_params),
                content=body,
                headers=headers,
            )
    except httpx.HTTPError as e:
        raise HTTPException(502, f"Модуль '{module['name']}' недоступен: {e}")
    return Response(
        content=resp.content,
        status_code=resp.status_code,
        media_type=resp.headers.get("content-type"),
    )


def module_by_legacy_route(path: str) -> Dict[str, Any]:
    for m in load_registry():
        if path in (m.get("legacy_routes") or []):
            return m
    raise HTTPException(404, f"Ни один модуль не обрабатывает маршрут {path}")


# ------------------------------------------------------------- API ----

@app.get("/health")
def health():
    modules = load_registry()
    up = 0
    detail = {}
    for m in modules:
        st = container_version(m)["status"]
        detail[m["name"]] = st
        if st == "up":
            up += 1
    return {
        "status": "ok",
        "gateway": True,
        "modules_total": len(modules),
        "modules_up": up,
        "modules": detail,
    }


@app.get("/modules")
def list_modules():
    """Реестр модулей с версиями: работающая vs последний релиз на GitHub."""
    out = []
    for m in load_registry():
        running = container_version(m)
        release = get_latest_release(m["repo"]) if m.get("repo") else {"version": None, "url": None, "error": None}
        cur, latest = running["version_running"], release["version"]
        update_available = (
            cur is not None and latest is not None
            and parse_version(latest) > parse_version(cur)
        )
        out.append({
            "name": m["name"],
            "display_name": m.get("display_name", m["name"]),
            "description": m.get("description", ""),
            "status": running["status"],
            "version_running": version_tuple_str(cur) if cur else None,
            "version_latest": version_tuple_str(latest) if latest else None,
            "update_available": update_available,
            "release_url": release.get("url"),
            "release_error": release.get("error"),
        })
    return {"modules": out}


@app.post("/modules/update")
def update_all():
    """Обновить все модули, для которых есть свежий релиз."""
    results = [update_module(m["name"]) for m in load_registry()]
    return {"results": results}


@app.post("/modules/{name}/update")
def update_module(name: str):
    """Обновить модуль до последнего релиза: pull образа + пересоздание контейнера."""
    m = get_module(name)

    release = get_latest_release(m["repo"])
    if release.get("error"):
        raise HTTPException(502, release["error"])
    if not release.get("version"):
        raise HTTPException(400, f"В репозитории {m['repo']} пока нет ни одного релиза")

    latest = release["version"]
    current = container_version(m)["version_running"]
    if current and parse_version(latest) <= parse_version(current):
        return {"module": name, "status": "already_latest", "version": version_tuple_str(current)}

    image_ref = f"{m['image']}:{latest}"
    try:
        docker_client().images.pull(*image_ref.rsplit(":", 1))
    except Exception as e:
        raise HTTPException(
            502,
            f"Не удалось скачать образ {image_ref}: {e}. "
            "Если пакет GHCR приватный — выполните на сервере: docker login ghcr.io",
        )

    try:
        recreate_container(m, image_ref)
    except Exception as e:
        raise HTTPException(500, f"Образ скачан, но контейнер не пересоздан: {e}")

    healthy = wait_healthy(m, timeout=60)
    return {
        "module": name,
        "status": "updated" if healthy else "updated_but_unhealthy",
        "from": version_tuple_str(current) if current else None,
        "to": version_tuple_str(latest),
        "release_url": release.get("url"),
    }


@app.api_route("/modules/{name}/proxy/{path:path}", methods=["GET", "POST", "PUT", "PATCH", "DELETE"])
async def module_proxy(name: str, path: str, request: Request):
    """Универсальный прокси к API модуля: /modules/managerdatabase/proxy/run -> модуль."""
    return await proxy_to_module(get_module(name), path, request)


# --- Совместимость со старыми клиентами (WPF HotkeyApp, прежний фронтенд) ---
# Маршруты /run и /annual_plan шлюз передаёт модулю, объявившему их в legacy_routes.

@app.post("/run")
async def legacy_run(request: Request):
    return await proxy_to_module(module_by_legacy_route("/run"), "run", request)


@app.post("/annual_plan")
async def legacy_annual_plan(request: Request):
    return await proxy_to_module(module_by_legacy_route("/annual_plan"), "annual_plan", request)


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000)
