import logging
import shutil
import tempfile
from pathlib import Path

NULL_LOGGER = logging.getLogger("pacodecopy.tests")
NULL_LOGGER.addHandler(logging.NullHandler())
NULL_LOGGER.propagate = False


class TempFolderMixin:
    """Gives each test its own temp folder (self.tmp) and deletes it afterwards."""

    def setUp(self) -> None:  # noqa: D401
        super().setUp()
        self.tmp = Path(tempfile.mkdtemp(prefix="pacodecopy-tests-"))
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
