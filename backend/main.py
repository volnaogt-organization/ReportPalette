"""
HTTP-сервис (бэкенд) поверх репозитория ManagerDataBase
(https://github.com/volnaogt-organization/ManagerDataBase).

Запуск (из папки backend/):
    pip install -r requirements.txt
    uvicorn main:app --host 127.0.0.1 --port 8000

WPF-клиент (HotkeyApp) обращается к этому сервису по HTTP/JSON.
"""

import os
import sys
import threading
from typing import List, Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

# --- Подключаем клонированный репозиторий ManagerDataBase как Python-модуль ---
# (репозиторий лежит в backend/ManagerDataBase и импортируется через `import scr...`)
MDB_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ManagerDataBase")
if MDB_ROOT not in sys.path:
    sys.path.insert(0, MDB_ROOT)

try:
    from scr.manager_database import MainLogic  # noqa: E402
except Exception as e:  # зависимости ManagerDataBase (openpyxl и т.п.) не установлены
    MainLogic = None
    IMPORT_ERROR = str(e)
else:
    IMPORT_ERROR = None

app = FastAPI(
    title="ManagerDataBase API",
    description="Обёртка над логикой ManagerDataBase для WPF-клиента HotkeyApp",
    version="0.1.0",
)

# Обработка ДСЕ идёт в одном экземпляре логики; блокировка защищает от параллельных запусков
_lock = threading.Lock()


class RunRequest(BaseModel):
    program: int = Field(..., description="Отчёт: 1010, 2012 или 365 (годовой план)")
    dse_list: List[str] = Field(default_factory=list,
                                description="Список ДСЕ, напр. ['ЛТИЯ.716713.001']")
    save_input: int = Field(0, description="1 — сохранить результат в Excel")
    max_threads: int = Field(6, ge=1, le=16)


class AnnualPlanRequest(BaseModel):
    path_to_excel_file: str


@app.get("/health")
def health():
    return {
        "status": "ok" if MainLogic is not None else "degraded",
        "managerdatabase_root": MDB_ROOT,
        "core_imported": MainLogic is not None,
        "import_error": IMPORT_ERROR,
    }


@app.post("/run")
def run(req: RunRequest):
    """Запуск обработки списка ДСЕ выбранным отчётом (как в консольном cmd_body.py)."""
    if MainLogic is None:
        raise HTTPException(503, f"Ядро ManagerDataBase не загружено: {IMPORT_ERROR}")
    if req.program not in (1010, 2012):
        raise HTTPException(400, "program должен быть 1010 или 2012 (для 365 используйте /annual_plan)")
    if not req.dse_list:
        raise HTTPException(400, "Пустой список ДСЕ")

    if not _lock.acquire(blocking=False):
        raise HTTPException(409, "Уже выполняется другая задача, попробуйте позже")
    try:
        logic = MainLogic()
        # MainLogic.main пишет результаты в shared dict и (опционально) в Excel.
        result_store: dict = {}
        logic.main(dse_list=req.dse_list,
                   user_selected_program=req.program,
                   save_input=req.save_input,
                   max_threads=req.max_threads)
        return {"status": "done", "program": req.program, "processed": len(req.dse_list)}
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(500, f"Ошибка при обработке: {e}")
    finally:
        _lock.release()


@app.post("/annual_plan")
def annual_plan(req: AnnualPlanRequest):
    """Загрузка годового отчёта из Excel-файла."""
    if MainLogic is None:
        raise HTTPException(503, f"Ядро ManagerDataBase не загружено: {IMPORT_ERROR}")
    if not os.path.isfile(req.path_to_excel_file):
        raise HTTPException(400, f"Файл не найден: {req.path_to_excel_file}")

    if not _lock.acquire(blocking=False):
        raise HTTPException(409, "Уже выполняется другая задача, попробуйте позже")
    try:
        MainLogic.start_annual_plan(req.path_to_excel_file)
        return {"status": "done"}
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(500, f"Ошибка при загрузке годового отчёта: {e}")
    finally:
        _lock.release()


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000)
