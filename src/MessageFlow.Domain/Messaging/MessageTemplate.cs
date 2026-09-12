using System.Text.RegularExpressions;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Messaging;

/// <summary>
/// Value Object inmutable que representa la plantilla de mensaje de un Flow.
/// Soporta variables dinámicas con sintaxis <c>{{NombreColumna}}</c>.
/// </summary>
public sealed partial record MessageTemplate
{
    public const int MaxLength = 8000;

    public string Text { get; }

    private MessageTemplate(string text) => Text = text;

    public static MessageTemplate Create(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidTemplateException("El mensaje del Flow no puede estar vacío.");
        }

        var trimmed = text.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new InvalidTemplateException($"El mensaje no puede superar {MaxLength} caracteres.");
        }

        return new MessageTemplate(trimmed);
    }

    /// <summary>Nombres de variables usados en la plantilla, sin duplicados y sin distinción de mayúsculas.</summary>
    public IReadOnlyList<string> ExtractVariableNames()
        => [.. VariableRegex()
            .Matches(Text)
            .Select(m => m.Groups["name"].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Devuelve las variables de la plantilla que NO existen como columnas del dataset.
    /// </summary>
    public IReadOnlyList<string> FindMissingVariables(IEnumerable<string> availableIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(availableIdentifiers);

        var known = availableIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. ExtractVariableNames().Where(name => !known.Contains(name))];
    }

    /// <summary>
    /// Normaliza los tokens de variable que coincidan con alguna clave del mapa,
    /// reemplazándolos por su valor canónico (p. ej. alias Id de columna → Nombre de
    /// columna). Los tokens sin alias quedan intactos para que la validación los
    /// reporte. Útil cuando las variables pueden expresarse por varios identificadores.
    /// </summary>
    public MessageTemplate WithVariableAliases(IReadOnlyDictionary<string, string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        if (aliases.Count == 0)
        {
            return this;
        }

        var hasKnownAlias = ExtractVariableNames().Any(v => aliases.ContainsKey(v));

        if (!hasKnownAlias)
        {
            return this;
        }

        var normalized = VariableRegex().Replace(Text, match =>
        {
            var token = match.Groups["name"].Value.Trim();

            return aliases.TryGetValue(token, out var canonical)
                ? Wrap(canonical)
                : match.Value;
        });

        return Create(normalized);
    }

    /// <param name="rowValues">
    /// Valores del registro actual. Debe usar StringComparer.OrdinalIgnoreCase.
    /// </param>
    public string Render(
        IReadOnlyDictionary<string, string?> rowValues,
        MissingVariablePolicy policy = MissingVariablePolicy.Fail)
    {
        ArgumentNullException.ThrowIfNull(rowValues);

        return VariableRegex().Replace(Text, match =>
        {
            var name = match.Groups["name"].Value.Trim();

            if (rowValues.TryGetValue(name, out var value) && value is not null)
            {
                return value;
            }

            return policy switch
            {
                MissingVariablePolicy.LeavePlaceholder => match.Value,
                MissingVariablePolicy.Empty => string.Empty,
                _ => throw new MissingTemplateVariableException(Wrap(name)),
            };
        });
    }

    internal static string Wrap(string variableName) => "{{" + variableName + "}}";

    [GeneratedRegex(@"\{\{\s*(?<name>[^{}\r\n]+?)\s*\}\}", RegexOptions.CultureInvariant, 250)]
    private static partial Regex VariableRegex();
}
