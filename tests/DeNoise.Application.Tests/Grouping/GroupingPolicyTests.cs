using DeNoise.Application.Grouping;
using DeNoise.Application.Mapping;

namespace DeNoise.Application.Tests.Grouping;

/// <summary>Adaptive window parsing (spec §16.3): scalar windows keep today's behaviour, extension needs a maximum, digests need an interval.</summary>
[Trait("Category", "Unit")]
public sealed class GroupingPolicyTests
{
    [Fact]
    public void Scalar_window_is_a_fixed_window_without_extension_grace_or_retro()
    {
        var doc = GroupingPolicyDocument.ParseYaml("grouping:\n  rules:\n    - { name: fixed, match: { exists: service }, key: [service], window: 10m, notify: first_only }", 1);
        var rule = doc.Rules.Single();
        rule.Window.Should().Be(new GroupWindow(TimeSpan.FromMinutes(10), null, TimeSpan.FromMinutes(10), TimeSpan.Zero));
        rule.Window.Extends.Should().BeFalse();
        rule.LookBack.Should().Be(TimeSpan.Zero);
        rule.MinMembers.Should().Be(1);
        rule.Digest.Should().BeNull();
        rule.Notify.Should().Be(GroupNotify.FirstOnly);
    }

    [Fact]
    public void Object_window_with_retro_min_members_and_digest_parses()
    {
        var doc = GroupingPolicyDocument.ParseYaml("""
            grouping:
              rules:
                - name: storm on one cluster
                  match: { eq: [environment, production] }
                  key: [resource_id, rule_id]
                  window: { initial: 10m, extend_by: 5m, max: 1h, late_join: 2m }
                  retro: { look_back: 5m }
                  min_members: 2
                  notify: digest
                  digest: { every: 5m, max_items: 20 }
            """, 1);
        var rule = doc.Rules.Single();
        rule.Window.Should().Be(new GroupWindow(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromMinutes(2)));
        rule.Window.Extends.Should().BeTrue();
        rule.LookBack.Should().Be(TimeSpan.FromMinutes(5));
        rule.MinMembers.Should().Be(2);
        rule.Notify.Should().Be(GroupNotify.Digest);
        rule.Digest.Should().Be(new GroupDigest(TimeSpan.FromMinutes(5), 20));
    }

    [Fact]
    public void Extension_without_a_maximum_is_refused()
    {
        var act = () => GroupingPolicyDocument.ParseYaml("grouping:\n  rules:\n    - { match: { exists: service }, key: [service], window: { initial: 10m, extend_by: 5m } }", 1);
        act.Should().Throw<MappingValidationException>().Which.Errors.Should().ContainSingle().Which.Path.Should().Be("$.grouping.rules[0].window.max");
    }

    [Fact]
    public void Bounds_and_digest_consistency_are_validated_with_paths()
    {
        var act = () => GroupingPolicyDocument.ParseYaml("""
            grouping:
              rules:
                - match: { exists: service }
                  key: [service]
                  window: { initial: 10m, extend_by: 5m, max: 5m, late_join: 30h }
                  retro: { look_back: 30h }
                  min_members: 0
                  notify: first_only
                  digest: { every: 0m, max_items: 0 }
                - match: { exists: service }
                  key: [service]
                  notify: digest
            """, 1);
        var paths = act.Should().Throw<MappingValidationException>().Which.Errors.Select(e => e.Path).ToList();
        paths.Should().Contain("$.grouping.rules[0].window.max").And.Contain("$.grouping.rules[0].window.late_join").And.Contain("$.grouping.rules[0].retro.look_back")
            .And.Contain("$.grouping.rules[0].min_members").And.Contain("$.grouping.rules[0].digest.every").And.Contain("$.grouping.rules[0].digest.max_items")
            .And.Contain("$.grouping.rules[0].notify").And.Contain("$.grouping.rules[1].digest");
    }
}
