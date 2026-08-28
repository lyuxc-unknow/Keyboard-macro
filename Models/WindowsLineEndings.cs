using System.Text;

namespace SimToAutoWirte.Models;

public static class WindowsLineEndings
{
    public const string NewLine = "\r\n";

    /// <summary>把 CRLF、孤立 CR 和孤立 LF 统一为 Windows CRLF。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var requiresNormalization = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' && (index == 0 || text[index - 1] != '\r'))
            {
                requiresNormalization = true;
                break;
            }

            if (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'))
            {
                requiresNormalization = true;
                break;
            }
        }

        if (!requiresNormalization)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                builder.Append(NewLine);
            }
            else if (character == '\n')
            {
                builder.Append(NewLine);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static int GetLogicalLength(string text)
    {
        var length = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            length++;
        }

        return length;
    }
}
