using System.Text;
using System.Text.Json.Nodes;

namespace DeNoise.Application.Mapping;

/// <summary><c>template</c> rule: <c>"…{$.path}…{field}…"</c>; missing values render empty (07 §1).</summary>
public static class TemplateRenderer
{
    public static IEnumerable<string> Placeholders(string template)
    {
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0) yield break;
            var close = template.IndexOf('}', open + 1);
            if (close < 0) yield break;
            var inner = template[(open + 1)..close];
            if (inner.Length > 0) yield return inner;
            i = close + 1;
        }
    }

    public static string Render(string template, Func<string, JsonNode?> resolve)
    {
        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, open - i);
            var reference = template[(open + 1)..close];
            sb.Append(ValueCoercion.AsDisplayString(resolve(reference)));
            i = close + 1;
        }
        return sb.ToString();
    }
}
