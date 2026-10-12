using PaCodeCopy.Core.Detection;

namespace PaCodeCopy.Tests;

public class PadScriptDetectorTests
{
    private readonly PadScriptDetector _detector = new(threshold: 3);

    [Fact]
    public void Multi_action_flow_is_detected_with_action_count_and_first_action()
    {
        var result = _detector.Analyze(Samples.ExcelLoop);

        Assert.True(result.IsPadScript);
        Assert.Equal(6, result.ActionCount);
        Assert.Equal("SET", result.FirstAction);
    }

    [Fact]
    public void Single_action_with_parameters_is_detected()
    {
        var result = _detector.Analyze(Samples.SingleMessageBox);

        Assert.True(result.IsPadScript);
        Assert.Equal(1, result.ActionCount);
        Assert.Equal("Display.ShowMessageDialog", result.FirstAction);
    }

    [Theory]
    [InlineData(Samples.IfElse)]
    [InlineData(Samples.Disabled)]
    [InlineData("SET Total TO Price * Quantity")]
    [InlineData("CALL ProcessInvoices")]
    [InlineData("LOOP LoopIndex FROM 1 TO 10 STEP 1\r\n    WAIT 1\r\nEND")]
    public void Pad_constructs_are_detected(string text)
    {
        Assert.True(_detector.Analyze(text).IsPadScript);
    }

    [Theory]
    [InlineData(Samples.CSharp)]
    [InlineData(Samples.Sql)]
    [InlineData(Samples.Json)]
    [InlineData(Samples.Prose)]
    [InlineData("https://make.powerautomate.com/environments")]
    [InlineData("System.Windows")]
    [InlineData("set counter to 0")]
    public void Other_text_is_not_detected(string text)
    {
        Assert.False(_detector.Analyze(text).IsPadScript);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void Empty_input_is_not_detected(string? text)
    {
        var result = _detector.Analyze(text);

        Assert.False(result.IsPadScript);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Threshold_controls_strictness()
    {
        const string text = "SET Counter TO 0";

        Assert.True(new PadScriptDetector(threshold: 3).Analyze(text).IsPadScript);
        Assert.False(new PadScriptDetector(threshold: 5).Analyze(text).IsPadScript);
    }

    [Fact]
    public void Control_repository_block_counts_as_pad()
    {
        var text = "# [ControlRepository][PowerAutomateDesktop]\n{ \"ControlRepositorySymbols\": [] }";

        Assert.True(_detector.Analyze(text).IsPadScript);
    }
}
