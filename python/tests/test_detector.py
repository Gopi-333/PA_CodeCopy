import unittest

from pacodecopy.detector import PadScriptDetector
from tests import samples


class PadScriptDetectorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.detector = PadScriptDetector(threshold=3)

    def test_multi_action_flow_is_detected_with_action_count_and_first_action(self):
        result = self.detector.analyze(samples.EXCEL_LOOP)
        self.assertTrue(result.is_pad_script)
        self.assertEqual(6, result.action_count)
        self.assertEqual("Excel.LaunchExcel", result.first_action)

    def test_single_action_with_parameters_is_detected(self):
        result = self.detector.analyze(samples.SINGLE_MESSAGE_BOX)
        self.assertTrue(result.is_pad_script)
        self.assertEqual(1, result.action_count)
        self.assertEqual("Display.ShowMessageDialog", result.first_action)

    def test_pad_constructs_are_detected(self):
        for text in (
            samples.IF_ELSE,
            samples.DISABLED,
            "SET Total TO Price * Quantity",
            "CALL ProcessInvoices",
            "LOOP LoopIndex FROM 1 TO 10 STEP 1\r\n    WAIT 1\r\nEND",
        ):
            with self.subTest(text=text):
                self.assertTrue(self.detector.analyze(text).is_pad_script)

    def test_other_text_is_not_detected(self):
        for text in (
            samples.CSHARP, samples.SQL, samples.JSON, samples.PROSE,
            "https://make.powerautomate.com/environments", "System.Windows", "set counter to 0",
        ):
            with self.subTest(text=text):
                self.assertFalse(self.detector.analyze(text).is_pad_script)

    def test_empty_input_is_not_detected(self):
        for text in (None, "", "   \r\n  "):
            with self.subTest(text=text):
                result = self.detector.analyze(text)
                self.assertFalse(result.is_pad_script)
                self.assertEqual(0, result.score)

    def test_first_action_falls_back_to_keyword(self):
        self.assertEqual("SET", self.detector.analyze("SET a TO 1\nSET b TO 2").first_action)

    def test_threshold_controls_strictness(self):
        self.assertTrue(PadScriptDetector(3).analyze("SET Counter TO 0").is_pad_script)
        self.assertFalse(PadScriptDetector(5).analyze("SET Counter TO 0").is_pad_script)

    def test_control_repository_block_counts_as_pad(self):
        text = '# [ControlRepository][PowerAutomateDesktop]\n{ "ControlRepositorySymbols": [] }'
        self.assertTrue(self.detector.analyze(text).is_pad_script)


if __name__ == "__main__":
    unittest.main()
