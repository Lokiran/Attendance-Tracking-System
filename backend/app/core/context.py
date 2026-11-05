from dataclasses import dataclass


@dataclass(frozen=True)
class RequestContext:
    request_id: str | None = None
    ip_address: str | None = None
    user_agent: str | None = None


SYSTEM_CONTEXT = RequestContext(request_id="system")
