using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Notifications;
using AlertHub.Application.Notifications.Templates;
using AlertHub.Domain.Notifications;
using TemplateRenderer = AlertHub.Application.Notifications.Templates.TemplateRenderer;

namespace AlertHub.Application.Tests.Notifications;

[Trait("Category", "Unit")]
public sealed class TemplateRendererTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly TemplateRenderer _renderer = new();

    private static JsonObject HostileSample()
    {
        var model = NotificationModel.Sample(T0);
        model["episode"]!["summary"] = "Disk \"C:\\\" full\nline two </script>{{ not_a_template }}";
        model["episode"]!["resource"]!["name"] = "srv-01, \"quoted\"";
        return model;
    }

    [Fact]
    public async Task Json_format_escapes_every_value_so_payload_text_cannot_break_the_document()
    {
        var body = """{ "title": "{{ episode.summary }}", "resource": "{{ episode.resource.name }}", "n": {{ episode.occurrenceCount }} }""";
        var rendered = await _renderer.RenderAsync(TemplateFormats.Json, body, "application/json", HostileSample());
        using var doc = JsonDocument.Parse(rendered.Body);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Disk \"C:\\\" full\nline two </script>{{ not_a_template }}");
        doc.RootElement.GetProperty("resource").GetString().Should().Be("srv-01, \"quoted\"");
        doc.RootElement.GetProperty("n").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Text_format_renders_verbatim_and_offers_escape_for_html_receivers()
    {
        var rendered = await _renderer.RenderAsync(TemplateFormats.Text, "raw: {{ episode.summary }}\nsafe: {{ episode.summary | escape }}", "text/plain", HostileSample());
        rendered.Body.Should().Contain("raw: Disk \"C:\\\" full");
        rendered.Body.Should().Contain("safe: Disk &quot;C:\\&quot; full").And.Contain("&lt;/script&gt;");
        rendered.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Only_the_model_is_reachable()
    {
        // No CLR members, no environment, no filters with side effects: unknown names render empty rather than leaking anything.
        var rendered = await _renderer.RenderAsync(TemplateFormats.Text, "[{{ episode.GetType }}][{{ environment }}][{{ episode.summary.Length }}]", "text/plain", NotificationModel.Sample(T0));
        rendered.Body.Should().Be("[][][]");
    }

    [Fact]
    public async Task Invalid_json_output_and_oversized_output_are_rejected()
    {
        var broken = () => _renderer.RenderAsync(TemplateFormats.Json, """{ "title": {{ episode.summary }} }""", "application/json", NotificationModel.Sample(T0));
        (await broken.Should().ThrowAsync<TemplateRenderException>()).WithMessage("*not valid JSON*");

        var huge = () => _renderer.RenderAsync(TemplateFormats.Text, "{% for i in (1..300000) %}x{% endfor %}", "text/plain", NotificationModel.Sample(T0));
        await huge.Should().ThrowAsync<TemplateRenderException>();
    }

    [Fact]
    public void Validation_reports_parse_errors_and_bad_formats()
    {
        var errors = new List<AlertHub.Application.Mapping.MappingValidationError>();
        TemplateRenderer.Validate("xml", "{{ if", errors);
        errors.Select(e => e.Path).Should().Contain("$.format").And.Contain("$.body");
    }

    [Fact]
    public async Task Built_in_templates_render_the_sample_to_their_advertised_shape()
    {
        var sample = HostileSample();
        foreach (var template in BuiltInTemplates.Create(T0))
        {
            var rendered = await _renderer.RenderAsync(template, sample);
            if (template.Format == TemplateFormats.Json)
            {
                using var doc = JsonDocument.Parse(rendered.Body);
                if (template.TemplateId == BuiltInTemplates.TeamsAdaptiveCard)
                {
                    var card = doc.RootElement.GetProperty("attachments")[0].GetProperty("content");
                    card.GetProperty("type").GetString().Should().Be("AdaptiveCard");
                    card.GetProperty("version").GetString().Should().Be("1.5");
                    card.GetProperty("body")[0].GetProperty("text").GetString().Should().StartWith("CRITICAL · Disk \"C:\\\" full");
                    card.GetProperty("actions")[0].GetProperty("url").GetString().Should().StartWith("https://alert-hub.example.invalid/episodes/");
                }
                if (template.TemplateId == BuiltInTemplates.SlackBlocks) doc.RootElement.GetProperty("blocks").GetArrayLength().Should().BeGreaterThan(2);
                if (template.TemplateId == BuiltInTemplates.GenericJson) doc.RootElement.GetProperty("episode").GetProperty("summary").GetString().Should().Contain("</script>");
            }
            else
            {
                rendered.Body.Should().StartWith("[AlertHub][critical] Disk \"C:\\\" full");
            }
        }
    }
}
