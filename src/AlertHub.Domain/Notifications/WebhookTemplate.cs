namespace AlertHub.Domain.Notifications;

public static class TemplateFormats
{
    /// <summary>The template is a JSON document; every <c>{{ }}</c> value is JSON-escaped so a payload field can never break it (ADR-7).</summary>
    public const string Json = "json";
    /// <summary>Free text; <c>{{ x | escape }}</c> where the receiver expects HTML.</summary>
    public const string Text = "text";
    public static readonly IReadOnlyList<string> All = [Json, Text];
}

/// <summary>One version of a webhook body template (<c>cfg.webhook_template</c>, 05 §6). Built-ins are read-only rows; versioned like policies (ADR-6).</summary>
public sealed class WebhookTemplate
{
    public Guid TemplateId { get; init; }
    public int Version { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Format { get; init; }
    public required string Body { get; init; }
    public string ContentType { get; init; } = "application/json";
    /// <summary>The body rendered against the stored sample at creation: proof it renders, and the preview shown in lists.</summary>
    public string? SampleOutput { get; set; }
    public bool Builtin { get; init; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive => ActivatedAt is not null && DeactivatedAt is null;
}
