using System.Text.RegularExpressions;

namespace MessageFlow.Infrastructure.Messaging;

/// <summary>Oculta números de teléfono en los logs: no deben quedar datos personales en claro.</summary>
internal static partial class PhoneMasker
{
    private const int VisibleDigits = 4;

    /// <summary>"+573001234567" → "+**********4567".</summary>
    public static string Mask(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return string.Empty;
        }

        var trimmed = phone.Trim();
        var prefix = trimmed.StartsWith('+') ? "+" : string.Empty;
        var body = trimmed.TrimStart('+');

        return body.Length <= VisibleDigits
            ? prefix + new string('*', body.Length)
            : prefix + new string('*', body.Length - VisibleDigits) + body[^VisibleDigits..];
    }

    /// <summary>Enmascara toda secuencia de 7 o más dígitos dentro de un texto libre (cuerpos de error).</summary>
    public static string MaskDigits(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : LongDigits().Replace(text, match => Mask(match.Value));

    [GeneratedRegex(@"\d{7,}")]
    private static partial Regex LongDigits();
}
