using DeNoise.Application.Mapping;
using DeNoise.Domain.Environments;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Tests.Mapping;

/// <summary>ADR-17: the environment values a mapping produces statically must be configured environment classes.</summary>
[Trait("Category", "Unit")]
public sealed class MappingEnvironmentsTests
{
    private static MappingDocument Doc(string environmentRule, string extra = "") => MappingParser.ParseYaml($$"""
        mapping:
          fields:
            event_type: { const: firing }
            source_alert_id: { path: "$.id" }
            rule_id: { path: "$.rule" }
            environment: {{environmentRule}}
        {{extra}}
          identity:
            - { name: rule, field: rule_id }
        """, 1);

    [Fact]
    public void Lookup_map_regex_map_and_default_targets_are_literals_with_editor_paths()
    {
        var doc = Doc("""{ lookup: { from: "$.sub", map: { "p-1": production }, regex_map: { "^s-.*": staging }, default: unknown } }""");

        MappingEnvironments.Literals(doc).Should().BeEquivalentTo(new[]
        {
            new MappingEnvironments.Literal("$.mapping.fields.environment.lookup.map.p-1", "production"),
            new MappingEnvironments.Literal("$.mapping.fields.environment.lookup.regex_map[0]", "staging"),
            new MappingEnvironments.Literal("$.mapping.fields.environment.lookup.default", "unknown"),
        });
    }

    [Fact]
    public void First_nonempty_candidates_are_walked_and_transforms_apply_inner_then_outer()
    {
        var doc = Doc("""{ first_nonempty: [ { const: " CI ", transform: [trim] }, { path: "$.env" } ], transform: [lower], default: Unknown }""");

        MappingEnvironments.Literals(doc).Select(l => (l.Path, l.Value)).Should().BeEquivalentTo(new[]
        {
            ("$.mapping.fields.environment.first_nonempty[0].const", "ci"),
            ("$.mapping.fields.environment.default", "unknown"),
        }, "a path copied from the payload is not knowable at save time");
    }

    [Fact]
    public void Enrich_lookup_tables_that_write_environment_are_checked()
    {
        var doc = Doc("{ path: \"$.env\" }", """
              lookup_tables:
                envs: { a: production, b: qa }
              enrich:
                - { set: environment, lookup_table: envs, key: rule_id, when_empty: true }
            """);

        MappingEnvironments.Literals(doc).Select(l => l.Value).Should().BeEquivalentTo(["production", "qa"]);
    }

    [Fact]
    public void A_value_outside_the_dictionary_is_an_error_at_its_path_naming_the_allowed_classes()
    {
        var doc = Doc("""{ lookup: { from: "$.sub", regex_map: { "^p-.*": prodcution, "^s-.*": staging }, default: unknown } }""");

        var errors = MappingEnvironments.Validate(doc, EnvironmentClasses.Seeded.ToArray());

        var error = errors.Should().ContainSingle().Subject;
        error.Path.Should().Be("$.mapping.fields.environment.lookup.regex_map[0]");
        error.Message.Should().Contain("'prodcution'").And.Contain("production, staging");
    }

    [Fact]
    public void The_generic_identity_mapping_copies_from_the_payload_and_only_its_default_is_checked()
    {
        MappingEnvironments.Literals(BuiltInMappings.GenericWebhook).Select(l => l.Value).Should().Equal(EnvironmentClasses.Unknown);
    }

    [Theory]
    [InlineData(IntegrationTypes.AzureMonitor)]
    [InlineData(IntegrationTypes.Atlas)]
    [InlineData(IntegrationTypes.AzureDevops)]
    [InlineData(IntegrationTypes.Github)]
    public void Every_reference_mapping_stays_inside_the_seeded_dictionary(string type)
    {
        foreach (var reference in ReferenceMappings.For(type))
        {
            var doc = MappingParser.ParseYaml(reference.Yaml, 1);
            MappingEnvironments.Validate(doc, EnvironmentClasses.Seeded.ToArray())
                .Should().BeEmpty($"{reference.Name} is seeded active on integration creation, which would otherwise be refused");
        }
    }

    [Theory]
    [InlineData("production", true)]
    [InlineData("pre-prod_2", true)]
    [InlineData("Production", false)]
    [InlineData("2nd", false)]
    [InlineData("", false)]
    [InlineData("a-very-long-environment-class-key", false)]
    public void Keys_are_lower_case_identifiers_of_at_most_32_characters(string key, bool valid)
        => EnvironmentClasses.IsValidKey(key).Should().Be(valid);
}
