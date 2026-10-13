import json
import unittest

from pacodecopy.config import AppSettings, SettingsStore
from tests.helpers import NULL_LOGGER, TempFolderMixin

# settings.json exactly as the C# app writes it (UTF-8, PascalCase keys).
CSHARP_SETTINGS = """{
  "SnippetsRootFolder": "C:\\\\users\\\\me\\\\Documents\\\\PaCodeCopy\\\\Snippets",
  "AutoCapture": false,
  "DetectionThreshold": 5,
  "DefaultCategory": "Excel",
  "MinimizeToTray": true,
  "ShowNotifications": false,
  "LogRetentionDays": 30
}"""


class SettingsStoreTests(TempFolderMixin, unittest.TestCase):
    def setUp(self) -> None:
        super().setUp()
        self.path = self.tmp / "settings.json"
        self.store = SettingsStore(self.path, self.tmp / "DefaultRoot", NULL_LOGGER)

    def test_missing_file_gives_defaults(self):
        settings = self.store.load()
        self.assertEqual(str(self.tmp / "DefaultRoot"), settings.snippets_root_folder)
        self.assertTrue(settings.auto_capture)
        self.assertEqual(3, settings.detection_threshold)

    def test_reads_settings_written_by_the_csharp_app_including_bom(self):
        self.path.write_bytes(b"\xef\xbb\xbf" + CSHARP_SETTINGS.encode("utf-8"))
        settings = self.store.load()
        self.assertEqual("C:\\users\\me\\Documents\\PaCodeCopy\\Snippets", settings.snippets_root_folder)
        self.assertEqual((False, 5, "Excel", False, 30),
                         (settings.auto_capture, settings.detection_threshold, settings.default_category,
                          settings.show_notifications, settings.log_retention_days))

    def test_save_writes_csharp_keys_and_keeps_unknown_keys(self):
        self.path.write_text(json.dumps({"AutoCapture": True, "FutureOption": 42}), encoding="utf-8")
        settings = self.store.load()
        settings.auto_capture = False
        self.store.save(settings)
        data = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertEqual(False, data["AutoCapture"])
        self.assertEqual(42, data["FutureOption"])
        self.assertEqual({"SnippetsRootFolder", "AutoCapture", "DetectionThreshold", "DefaultCategory",
                          "MinimizeToTray", "ShowNotifications", "LogRetentionDays"} - set(data), set())

    def test_broken_file_gives_defaults_and_out_of_range_values_are_clamped(self):
        self.path.write_text("{ not json", encoding="utf-8")
        self.assertTrue(self.store.load().auto_capture)
        self.path.write_text(json.dumps({"DetectionThreshold": 99, "LogRetentionDays": 0}), encoding="utf-8")
        settings = self.store.load()
        self.assertEqual((20, 1), (settings.detection_threshold, settings.log_retention_days))

    def test_normalize_fills_empty_values(self):
        settings = AppSettings(snippets_root_folder=" ", default_category="")
        settings.normalize(self.tmp)
        self.assertEqual((str(self.tmp), "General"), (settings.snippets_root_folder, settings.default_category))


if __name__ == "__main__":
    unittest.main()
