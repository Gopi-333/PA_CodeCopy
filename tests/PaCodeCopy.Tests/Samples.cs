namespace PaCodeCopy.Tests;

/// <summary>Text in the format PAD puts on the clipboard when actions are copied.</summary>
internal static class Samples
{
    public const string ExcelLoop = """
        SET Counter TO 0
        Excel.LaunchExcel.LaunchAndOpenUnderExistingProcess Path: $'''C:\Data\Input.xlsx''' Visible: True ReadOnly: False Instance=> ExcelInstance
        Excel.ReadFromExcel.ReadAllCells Instance: ExcelInstance ReadAsText: False FirstLineIsHeader: True RangeValue=> ExcelData
        LOOP FOREACH CurrentItem IN ExcelData
            Variables.IncreaseVariable Value: Counter IncrementValue: 1
        END
        Excel.CloseExcel.Close Instance: ExcelInstance
        """;

    public const string SingleMessageBox =
        "Display.ShowMessageDialog.ShowMessage Title: $'''Info''' Message: $'''Done: %Counter%''' Icon: Display.Icon.None " +
        "Buttons: Display.Buttons.OK DefaultButton: Display.DefaultButton.Button1 IsTopMost: False ButtonPressed=> ButtonPressed";

    public const string IfElse = """
        IF Counter > 5 THEN
            File.WriteText File: $'''C:\Temp\log.txt''' TextToWrite: Counter AppendNewLine: True IfFileExists: File.IfFileExists.Append Encoding: File.FileEncoding.Unicode
        ELSE
            WAIT 2
        END
        """;

    public const string Disabled = "DISABLE File.Delete Files: $'''C:\\Temp\\old.txt'''";

    public const string CSharp = """
        using System;
        Console.WriteLine("Hello");
        var total = items.Sum(x => x.Price);
        """;

    public const string Sql = """
        SET NOCOUNT ON;
        SELECT * FROM Users WHERE Id = 1
        IF @x = 1 BEGIN
        END
        """;

    public const string Json = """{ "name": "flow", "actions": [ { "type": "Compose" } ] }""";

    public const string Prose = "Hi team,\nPlease review the invoice process today.\nThanks.";
}
