"""Decides whether text is Power Automate Desktop code.

Port of PadScriptDetector.cs - keep the patterns and scores identical in both apps.

When you copy actions in the PAD designer (Ctrl+C), PAD puts them on the clipboard as text:

    SET Counter TO 0
    Excel.LaunchExcel.LaunchAndOpenUnderExistingProcess Path: $'''C:\\In.xlsx''' Visible: True Instance=> ExcelInstance
    LOOP FOREACH Row IN ExcelData
        Variables.IncreaseVariable Value: Counter IncrementValue: 1
    END
"""

from __future__ import annotations

import re
from dataclasses import dataclass

STRONG_STATEMENT_SCORE = 3
ACTION_SCORE = 3
MARKER_SCORE = 1
ANNOTATION_SCORE = 2
CONTROL_REPOSITORY_SCORE = 3
MAX_LINES_TO_SCAN = 2000


@dataclass(frozen=True)
class DetectionResult:
    is_pad_script: bool
    score: int
    action_count: int
    # First module action ("Excel.ReadFromExcel"), else first keyword ("SET"). Used for default names.
    first_action: str | None


def _p(pattern: str) -> re.Pattern[str]:
    return re.compile(pattern)


_DISABLE_PREFIX = _p(r"^\s*DISABLE\s+")

# Case-sensitive on purpose: PAD writes keywords in upper case (keeps SQL "SET NOCOUNT ON" out).
_STATEMENTS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("SET", _p(r"^SET\s+\S+\s+TO\b")),
    ("IF", _p(r"^IF\s+.+\s+THEN\s*$")),
    ("ELSE IF", _p(r"^ELSE\s+IF\s+.+\s+THEN\s*$")),
    ("LOOP", _p(r"^LOOP\s+\S+\s+FROM\s+.+\s+TO\s+.+\s+STEP\s+\S+")),
    ("LOOP WHILE", _p(r"^LOOP\s+WHILE\s+.+")),
    ("FOREACH", _p(r"^(LOOP\s+)?FOREACH\s+\S+\s+IN\s+\S+")),
    ("SWITCH", _p(r"^SWITCH\s+\S+")),
    ("CASE", _p(r"^CASE\s+.+")),
    ("DEFAULT", _p(r"^DEFAULT\s*$")),
    ("CALL", _p(r"^CALL\s+\S+")),
    ("LABEL", _p(r"^LABEL\s+\S+")),
    ("GOTO", _p(r"^GOTO\s+\S+")),
    ("BLOCK", _p(r"^BLOCK\s+\S+")),
    ("ON BLOCK ERROR", _p(r"^ON\s+BLOCK\s+ERROR\b")),
    ("ON ERROR", _p(r"^ON\s+ERROR\b")),
    ("WAIT", _p(r"^WAIT\s+\S+")),
    ("EXIT", _p(r"^EXIT\s+(LOOP|FUNCTION|Code:)")),
    ("NEXT LOOP", _p(r"^NEXT\s+LOOP\s*$")),
    ("FUNCTION", _p(r"^FUNCTION\s+\S+\s+GLOBAL\b")),
)

_WEAK_STATEMENT = _p(r"^(END(\s+\w+)?|ELSE|END FUNCTION)\s*$")

# Module.Action[.Variant] followed by "Param: value" / "Output=> var", or alone on the line.
_ACTION_LINE = _p(
    r"^(?P<action>[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*){1,3})(?P<rest>\s+[A-Za-z][A-Za-z0-9]*\s*(?::|=>).*|\s*)$"
)

_OUTPUT_BINDING = _p(r"[A-Za-z0-9]=>\s*\S")
_VERBATIM_STRING = _p(r"\$'''")
_VARIABLE_REFERENCE = _p(r"%[A-Za-z_][A-Za-z0-9_.\[\]'\"]*%")
_ANNOTATION = _p(r"^@@\S+")
_CONTROL_REPOSITORY = _p(r"^#\s*\[ControlRepository\]")


class PadScriptDetector:
    def __init__(self, threshold: int = 3) -> None:
        self.threshold = threshold

    @property
    def threshold(self) -> int:
        return self._threshold

    @threshold.setter
    def threshold(self, value: int) -> None:
        self._threshold = max(1, int(value))

    def analyze(self, text: str | None) -> DetectionResult:
        if text is None or not text.strip():
            return DetectionResult(False, 0, 0, None)

        score = 0
        action_count = 0
        first_keyword: str | None = None
        first_module_action: str | None = None
        has_control_repository = False

        for raw_line in text.split("\n")[:MAX_LINES_TO_SCAN]:
            line = _DISABLE_PREFIX.sub("", raw_line.strip(), count=1)
            if not line:
                continue

            if _CONTROL_REPOSITORY.match(line):
                has_control_repository = True
                score += CONTROL_REPOSITORY_SCORE
                continue

            if _ANNOTATION.match(line):
                score += ANNOTATION_SCORE
                continue

            keyword = _match_statement(line)
            if keyword is not None:
                score += STRONG_STATEMENT_SCORE + _marker_score(line)
                action_count += 1
                first_keyword = first_keyword or keyword
                continue

            if _WEAK_STATEMENT.match(line):
                score += MARKER_SCORE
                continue

            action = _ACTION_LINE.match(line)
            if action:
                has_parameters = bool(action.group("rest").strip())
                # A bare "Word.Word" line is too ambiguous to count on its own.
                score += ACTION_SCORE if has_parameters else MARKER_SCORE
                if has_parameters:
                    action_count += 1
                    first_module_action = first_module_action or _short_action_name(action.group("action"))
                score += _marker_score(line)

        is_pad = score >= self.threshold and (action_count > 0 or has_control_repository)
        return DetectionResult(is_pad, score, action_count, first_module_action or first_keyword)


def _match_statement(line: str) -> str | None:
    for keyword, pattern in _STATEMENTS:
        if pattern.match(line):
            return keyword
    return None


def _marker_score(line: str) -> int:
    markers = sum(1 for pattern in (_OUTPUT_BINDING, _VERBATIM_STRING, _VARIABLE_REFERENCE) if pattern.search(line))
    return markers * MARKER_SCORE


def _short_action_name(full_name: str) -> str:
    """'Excel.ReadFromExcel.ReadAllCells' -> 'Excel.ReadFromExcel'."""
    parts = full_name.split(".")
    return f"{parts[0]}.{parts[1]}" if len(parts) > 2 else full_name
