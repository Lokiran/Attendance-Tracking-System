from fastapi import APIRouter
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError

from app.api.deps import DB
from app.core import clock

router = APIRouter(prefix="/health", tags=["health"])


@router.get("", summary="Liveness")
def health():
    return {"status": "ok", "time": clock.utcnow()}


@router.get("/db", summary="Database connectivity")
def health_db(db: DB):
    try:
        db.execute(text("SELECT 1"))
    except SQLAlchemyError:
        return JSONResponse(status_code=503, content={"status": "unavailable", "database": "unreachable"})
    return {"status": "ok", "database": db.get_bind().dialect.name}
