"""Text in the format PAD puts on the clipboard when actions are copied (same as the C# tests)."""

EXCEL_LOOP = """SET Counter TO 0
Excel.LaunchExcel.LaunchAndOpenUnderExistingProcess Path: $'''C:\\Data\\Input.xlsx''' Visible: True ReadOnly: False Instance=> ExcelInstance
Excel.ReadFromExcel.ReadAllCells Instance: ExcelInstance ReadAsText: False FirstLineIsHeader: True RangeValue=> ExcelData
LOOP FOREACH CurrentItem IN ExcelData
    Variables.IncreaseVariable Value: Counter IncrementValue: 1
END
Excel.CloseExcel.Close Instance: ExcelInstance"""

SINGLE_MESSAGE_BOX = (
    "Display.ShowMessageDialog.ShowMessage Title: $'''Info''' Message: $'''Done: %Counter%''' Icon: Display.Icon.None "
    "Buttons: Display.Buttons.OK DefaultButton: Display.DefaultButton.Button1 IsTopMost: False ButtonPressed=> ButtonPressed"
)

IF_ELSE = """IF Counter > 5 THEN
    File.WriteText File: $'''C:\\Temp\\log.txt''' TextToWrite: Counter AppendNewLine: True IfFileExists: File.IfFileExists.Append Encoding: File.FileEncoding.Unicode
ELSE
    WAIT 2
END"""

DISABLED = "DISABLE File.Delete Files: $'''C:\\Temp\\old.txt'''"

CSHARP = """using System;
Console.WriteLine("Hello");
var total = items.Sum(x => x.Price);"""

SQL = """SET NOCOUNT ON;
SELECT * FROM Users WHERE Id = 1
IF @x = 1 BEGIN
END"""

JSON = '{ "name": "flow", "actions": [ { "type": "Compose" } ] }'

PROSE = "Hi team,\nPlease review the invoice process today.\nThanks."
