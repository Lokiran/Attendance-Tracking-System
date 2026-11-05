"""Turn failed tests in a JUnit XML report into GitHub Actions error annotations.

Annotations appear on the run summary and pull request, and are readable through the public
checks API, so failures can be diagnosed without downloading logs.
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def escape(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def main(path: str) -> int:
    report = Path(path)
    if not report.exists():
        print(f"::warning title=No test report::{path} was not produced (the job failed before tests ran)")
        return 0
    failures = 0
    for case in ET.parse(report).getroot().iter("testcase"):
        for problem in list(case.findall("failure")) + list(case.findall("error")):
            failures += 1
            name = f"{case.get('classname', '')}.{case.get('name', '')}".strip(".")
            detail = (problem.get("message") or "") + "\n" + (problem.text or "")
            # Keep the end of the traceback: that is where the assertion and error live.
            print(f"::error title={escape(name)[:200]}::{escape(detail.strip()[-3000:])}")
    print(f"{failures} failing test(s) annotated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1]))
