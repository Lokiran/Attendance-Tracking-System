"""Turn failed tests in a JUnit XML report into GitHub Actions error annotations.

Annotations appear on the run summary and pull request, and are readable through the public
checks API, so failures can be diagnosed without downloading logs. GitHub shows at most 10
error annotations per step, so the first one lists every failing test by name.
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

MAX_DETAIL = 2500


def escape(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def detail(problem: ET.Element) -> str:
    """Error message first (that is the useful part), then the end of the traceback."""
    message = (problem.get("message") or "").strip()
    trace = (problem.text or "").strip()
    room = MAX_DETAIL - len(message)
    if room > 200 and trace:
        tail = trace[-room:]
        return f"{message}\n...\n{tail}" if len(trace) > room else f"{message}\n{trace}"
    return message[:MAX_DETAIL]


def main(path: str) -> int:
    report = Path(path)
    if not report.exists():
        print(f"::warning title=No test report::{path} was not produced (the job failed before tests ran)")
        return 0
    failed: list[tuple[str, ET.Element]] = []
    for case in ET.parse(report).getroot().iter("testcase"):
        for problem in list(case.findall("failure")) + list(case.findall("error")):
            name = f"{case.get('classname', '')}.{case.get('name', '')}".strip(".")
            failed.append((name, problem))
    if failed:
        listing = "\n".join(f"- {name}" for name, _ in failed)
        print(f"::error title={len(failed)} failing test(s)::{escape(listing)}")
    for name, problem in failed[:8]:
        print(f"::error title={escape(name)[:200]}::{escape(detail(problem))}")
    print(f"{len(failed)} failing test(s) annotated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1]))
