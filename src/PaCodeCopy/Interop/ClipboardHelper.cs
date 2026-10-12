using System.Runtime.InteropServices;
using PaCodeCopy.Core.Logging;

namespace PaCodeCopy.Interop;

/// <summary>
/// Clipboard access with retries. Another process (often the one that just copied) can hold the
/// clipboard open for a moment, which makes the first read throw <see cref="ExternalException"/>.
/// </summary>
internal static class ClipboardHelper
{
    private const int MaxAttempts = 3;
    private const int RetryDelayMs = 100;

    /// <summary>Returns clipboard text, or null if there is no text or the clipboard stays locked.</summary>
    public static string? TryGetText(IAppLogger logger)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : null;
            }
            catch (ExternalException ex)
            {
                if (attempt == MaxAttempts)
                {
                    logger.Warn("Clipboard is locked by another application; read skipped.", ex);
                    return null;
                }

                Thread.Sleep(RetryDelayMs);
            }
        }

        return null;
    }

    /// <summary>Puts text on the clipboard so it can be pasted into the PAD designer.</summary>
    public static bool TrySetText(string text, IAppLogger logger)
    {
        try
        {
            // copy: true keeps the text after this app exits; built-in retry handles short locks.
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true, MaxAttempts, RetryDelayMs);
            return true;
        }
        catch (ExternalException ex)
        {
            logger.Warn("Could not write to the clipboard.", ex);
            return false;
        }
    }
}
