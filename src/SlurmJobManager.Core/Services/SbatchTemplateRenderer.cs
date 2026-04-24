namespace SlurmJobManager.Core.Services;

/// <summary>
/// Renders an sbatch script by substituting named tokens in a template string.
/// Tokens are encoded as <c>{{KEY}}</c>.
/// </summary>
public class SbatchTemplateRenderer
{
    private readonly string _templateContent;

    public SbatchTemplateRenderer(string templateContent)
    {
        _templateContent = templateContent ?? throw new ArgumentNullException(nameof(templateContent));
    }

    /// <summary>
    /// Replaces every <c>{{key}}</c> placeholder found in the template with the
    /// corresponding value from <paramref name="parameters"/>.
    /// Keys are matched case-insensitively.
    /// Any placeholder that has no matching key is left unchanged.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string> parameters)
    {
        var result = _templateContent;
        foreach (var (key, value) in parameters)
        {
            result = result.Replace($"{{{{{key}}}}}", value, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>
    /// Extracts all distinct placeholder keys (without braces) from the template.
    /// </summary>
    public IReadOnlyList<string> ExtractPlaceholders()
    {
        var keys = new List<string>();
        int pos = 0;
        while (true)
        {
            int start = _templateContent.IndexOf("{{", pos, StringComparison.Ordinal);
            if (start < 0) break;
            int end = _templateContent.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) break;
            var key = _templateContent.Substring(start + 2, end - start - 2).Trim();
            if (!string.IsNullOrEmpty(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                keys.Add(key);
            pos = end + 2;
        }
        return keys;
    }
}
