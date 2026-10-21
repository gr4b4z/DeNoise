using DeNoise.Application.Mapping;
using DeNoise.Application.Topology;
using DeNoise.Domain.Common;

namespace DeNoise.Application.Tests.Topology;

/// <summary>The <c>topology</c> policy kind (spec §16.5): parents from field equality or a static list, bounded windows, cycles refused.</summary>
[Trait("Category", "Unit")]
public sealed class TopologyPolicyTests
{
    [Fact]
    public void Parses_rules_with_defaults_that_never_mute_critical()
    {
        var doc = TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - name: host down suppresses its services
                  parent: { match: { eq: [rule_id, host-down] }, key: [resource_id] }
                  child:  { match: { neq: [rule_id, host-down] }, key: [labels.host] }
                  window: 15m
                  look_back: 2m
                  action: mute_child
                - name: cluster down suppresses its nodes
                  parent: { static: ["atlas-cluster-0", "atlas-cluster-1"], key: [resource_id] }
                  child:  { match: { eq: [dimensions.cluster, "atlas-cluster-0"] }, key: [] }
                  window: 30m
                  action: group_under_parent
                  mute_severities: [critical, high]
                - name: observe
                  parent: { match: { eq: [service, edge] }, key: [environment] }
                  child:  { match: { eq: [service, api] }, key: [environment] }
            """, 1);
        doc.Rules.Should().HaveCount(3);
        var host = doc.Rules[0];
        host.Window.Should().Be(TimeSpan.FromMinutes(15));
        host.LookBack.Should().Be(TimeSpan.FromMinutes(2));
        host.Action.Should().Be(TopologyActions.MuteChild);
        host.MuteSeverities.Should().Equal(Severity.High, Severity.Medium, Severity.Low, Severity.Informational);
        host.Mutes(Severity.Critical).Should().BeFalse("critical is muted only when a rule names it");
        host.Mutes(Severity.Medium).Should().BeTrue();
        var cluster = doc.Rules[1];
        cluster.Parent.IsStatic.Should().BeTrue();
        cluster.Parent.Match.Should().BeOfType<Predicate.In>().Which.Values.Should().HaveCount(2);
        cluster.Child.Key.Should().BeEmpty("a static parent already names the key value");
        cluster.MuteSeverities.Should().Equal(Severity.Critical, Severity.High);
        var observe = doc.Rules[2];
        observe.Action.Should().Be(TopologyActions.AnnotateOnly, "observation mode is the default (spec §16.5)");
        observe.LookBack.Should().Be(TimeSpan.Zero);
        observe.Window.Should().Be(TopologyPolicyDocument.DefaultWindow);
    }

    [Fact]
    public void Validation_errors_carry_paths()
    {
        var act = () => TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - parent: { match: { eq: [rule_id, a] } }
                  child:  { match: { eq: [rule_id, b] }, key: [labels.x, labels.y] }
                  action: delete
                  window: 48h
                  release: on_window_end
                - parent: { static: [x], match: { eq: [rule_id, a] }, key: [resource_id] }
                  child:  { key: [labels.x] }
            """, 1);
        var paths = act.Should().Throw<MappingValidationException>().Which.Errors.Select(e => e.Path).ToList();
        paths.Should().Contain("$.topology.rules[0].parent.key").And.Contain("$.topology.rules[0].action").And.Contain("$.topology.rules[0].window").And.Contain("$.topology.rules[0].release")
            .And.Contain("$.topology.rules[1].parent").And.Contain("$.topology.rules[1].child.match");
    }

    [Fact]
    public void Child_key_must_match_the_parent_key_position_by_position()
    {
        var act = () => TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - parent: { match: { eq: [rule_id, a] }, key: [resource_id, environment] }
                  child:  { match: { eq: [rule_id, b] }, key: [labels.host] }
            """, 1);
        act.Should().Throw<MappingValidationException>().Which.Errors.Should().ContainSingle().Which.Path.Should().Be("$.topology.rules[0].child.key");
    }

    [Fact]
    public void A_two_rule_cycle_is_refused_with_the_first_rule_of_the_cycle()
    {
        var act = () => TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - name: db suppresses app
                  parent: { match: { eq: [rule_id, db-down] }, key: [resource_id] }
                  child:  { match: { eq: [rule_id, app-down] }, key: [labels.db] }
                - name: app suppresses db
                  parent: { match: { eq: [rule_id, app-down] }, key: [resource_id] }
                  child:  { match: { eq: [rule_id, db-down] }, key: [labels.app] }
            """, 1);
        var error = act.Should().Throw<MappingValidationException>().Which.Errors.Should().ContainSingle().Subject;
        error.Path.Should().Be("$.topology.rules[0]");
        error.Message.Should().Contain("db suppresses app → app suppresses db → db suppresses app");
    }

    [Fact]
    public void A_self_dependency_and_an_in_list_that_contains_the_child_are_cycles_too()
    {
        var self = () => TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - parent: { match: { exists: service }, key: [service] }
                  child:  { match: { exists: service }, key: [service] }
            """, 1);
        self.Should().Throw<MappingValidationException>().WithMessage("*cycle*");

        var viaIn = () => TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - parent: { match: { in: [rule_id, [a, b]] }, key: [resource_id] }
                  child:  { match: { eq: [rule_id, b] }, key: [labels.host] }
            """, 1);
        viaIn.Should().Throw<MappingValidationException>().WithMessage("*cycle*", "a child with rule_id b is inside the parent's in-list");
    }

    [Fact]
    public void A_chain_is_not_a_cycle()
    {
        var doc = TopologyPolicyDocument.ParseYaml("""
            topology:
              rules:
                - name: cluster suppresses hosts
                  parent: { match: { eq: [rule_id, cluster-down] }, key: [resource_id] }
                  child:  { match: { eq: [rule_id, host-down] }, key: [labels.cluster] }
                - name: host suppresses services
                  parent: { match: { eq: [rule_id, host-down] }, key: [resource_id] }
                  child:  { match: { neq: [rule_id, host-down] }, key: [labels.host] }
            """, 1);
        doc.Rules.Should().HaveCount(2, "cluster → host → services is a chain; services are never cluster parents by predicate");
    }
}
