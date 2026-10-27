import sys
import unittest

from pacodecopy.padcontext import detect_flow_name, parse_flow_name


class PadContextTests(unittest.TestCase):
    def test_flow_name_is_read_from_the_window_title(self):
        cases = {
            "Invoice processing | Power Automate": "Invoice processing",
            "*Invoice processing | Power Automate": "Invoice processing",
            "Daily report - Power Automate": "Daily report",
            "JustAName": "JustAName",
            "": "PAD flow",
            None: "PAD flow",
        }
        for title, expected in cases.items():
            with self.subTest(title=title):
                self.assertEqual(expected, parse_flow_name(title))

    @unittest.skipIf(sys.platform == "win32", "on Windows the result depends on the open windows")
    def test_detect_returns_none_outside_windows(self):
        self.assertIsNone(detect_flow_name())

    @unittest.skipUnless(sys.platform == "win32", "Windows only")
    def test_detect_does_not_fail_on_windows(self):
        # No PAD designer runs on the build machine, so there is no flow to find.
        self.assertIsNone(detect_flow_name())


if __name__ == "__main__":
    unittest.main()
