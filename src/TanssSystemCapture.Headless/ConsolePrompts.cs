using System.Text;

namespace TanssSystemCapture.Headless;

internal static class ConsolePrompts
{
    public static string ReadRequired(string prompt, string? defaultValue = null)
    {
        while (true)
        {
            Console.Write(prompt);
            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                Console.Write($" [{defaultValue}]");
            }

            Console.Write(": ");
            var value = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                value = defaultValue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            Console.Error.WriteLine("Eine Eingabe ist erforderlich.");
        }
    }

    public static string ReadTotp()
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Die TOTP-Eingabe ist bei umgeleiteter Standardeingabe gesperrt. Bitte das Tool in einem interaktiven Terminal starten.");
        }

        Console.Write("TOTP-Code: ");
        var value = new StringBuilder(6);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
            {
                value.Length--;
                continue;
            }

            if (char.IsAsciiDigit(key.KeyChar) && value.Length < 6)
            {
                value.Append(key.KeyChar);
            }
        }

        return value.ToString();
    }

    public static bool ConfirmTransfer()
    {
        Console.WriteLine();
        Console.Write("Zum verbindlichen Schreiben nach TANSS exakt UEBERTRAGEN eingeben: ");
        return string.Equals(
            Console.ReadLine()?.Trim(),
            "UEBERTRAGEN",
            StringComparison.Ordinal);
    }
}
