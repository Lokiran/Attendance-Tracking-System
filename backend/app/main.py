"""FastAPI application factory."""

import logging
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from sqlalchemy.exc import IntegrityError, OperationalError
from starlette.exceptions import HTTPException as StarletteHTTPException

from app.api.routes import admin, attendance, auth, dashboard, health, reports, requests
from app.core.config import describe_database, get_settings, selected_env_file
from app.core.errors import AppError, TooManyRequests

logger = logging.getLogger("ats")


def _error(status: int, code: str, message: str, request: Request, details=None, headers=None) -> JSONResponse:
    body = {"error": {"code": code, "message": message}, "request_id": getattr(request.state, "request_id", None)}
    if details is not None:
        body["error"]["details"] = details
    return JSONResponse(status_code=status, content=body, headers=headers)


@asynccontextmanager
async def lifespan(_app: FastAPI):
    from app.db.base import Base  # tables are registered by the route/service imports above
    from app.db.session import engine, ensure_sqlite_ready

    logger.info("database: %s (env file: %s)",
                describe_database(engine.url.render_as_string(hide_password=False)), selected_env_file())
    ensure_sqlite_ready(engine, set(Base.metadata.tables))
    yield


def create_app() -> FastAPI:
    settings = get_settings()
    logging.basicConfig(level=settings.log_level, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    app = FastAPI(
        lifespan=lifespan,
        title="Office Attendance Tracker API",
        version="1.0.0",
        description="Attendance tracking with employee, manager and organisation dashboards. "
                    "Authenticate via POST /api/auth/login (HttpOnly session cookie); send the CSRF token "
                    "in the X-CSRF-Token header for state-changing requests.",
        docs_url="/api/docs",
        redoc_url="/api/redoc",
        openapi_url="/api/openapi.json",
    )

    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.cors_origin_list,
        allow_credentials=True,
        allow_methods=["GET", "POST", "PUT", "PATCH", "DELETE"],
        allow_headers=["Content-Type", settings.csrf_header_name, "X-Request-ID"],
        expose_headers=["Content-Disposition", "X-Request-ID"],
    )

    @app.middleware("http")
    async def request_context(request: Request, call_next):
        incoming = request.headers.get("x-request-id", "")
        request.state.request_id = incoming[:64] if incoming.isprintable() and incoming else uuid.uuid4().hex
        response = await call_next(request)
        response.headers["X-Request-ID"] = request.state.request_id
        response.headers.setdefault("X-Content-Type-Options", "nosniff")
        response.headers.setdefault("X-Frame-Options", "DENY")
        response.headers.setdefault("Referrer-Policy", "same-origin")
        if request.url.path.startswith("/api/") and not request.url.path.startswith(("/api/docs", "/api/redoc")):
            response.headers.setdefault("Cache-Control", "no-store")
        return response

    @app.exception_handler(AppError)
    async def app_error(request: Request, exc: AppError):
        headers = {"Retry-After": str(exc.retry_after_seconds)} if isinstance(exc, TooManyRequests) else None
        return _error(exc.status_code, exc.code, exc.message, request, exc.details, headers)

    @app.exception_handler(RequestValidationError)
    async def validation_error(request: Request, exc: RequestValidationError):
        details = [{"field": ".".join(str(p) for p in e.get("loc", []) if p != "body"), "message": e.get("msg")}
                   for e in exc.errors()]
        first = details[0] if details else None
        message = f"{first['field']}: {first['message']}" if first and first["field"] else "Invalid request"
        return _error(422, "validation_error", message, request, details)

    @app.exception_handler(StarletteHTTPException)
    async def http_error(request: Request, exc: StarletteHTTPException):
        code = {404: "not_found", 405: "method_not_allowed"}.get(exc.status_code, "http_error")
        return _error(exc.status_code, code, str(exc.detail), request)

    @app.exception_handler(IntegrityError)
    async def integrity_error(request: Request, exc: IntegrityError):
        # Backstop: constraint violations not translated by a service become 409, never 500.
        logger.warning("integrity error request_id=%s: %s", getattr(request.state, "request_id", None),
                       exc.orig.__class__.__name__)
        return _error(409, "conflict", "The change conflicts with existing data.", request)

    @app.exception_handler(OperationalError)
    async def db_unavailable(request: Request, exc: OperationalError):
        logger.error("database error: %s", exc.__class__.__name__)
        return _error(503, "database_unavailable", "The database is temporarily unavailable. Please retry.", request)

    @app.exception_handler(Exception)
    async def unexpected(request: Request, exc: Exception):
        logger.exception("unhandled error request_id=%s", getattr(request.state, "request_id", None))
        return _error(500, "internal_error", "An unexpected error occurred.", request)

    for module in (health, auth, attendance, requests, dashboard, admin, reports):
        app.include_router(module.router, prefix="/api")
    return app


app = create_app()
