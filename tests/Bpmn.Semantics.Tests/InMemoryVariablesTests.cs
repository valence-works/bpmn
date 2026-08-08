using Bpmn.Model;
using Bpmn.Runtime.InMemory;
using Shouldly;
using Xunit;

namespace Bpmn.Semantics.Tests;

/// <summary>
/// The variable reader. The interesting part is that "I have never heard of it", "I have it and it is null"
/// and "I have it but not where you can read it" are three different answers, and the port depends on them
/// staying different.
/// </summary>
public sealed class InMemoryVariablesTests
{
    private readonly InMemoryVariables _variables = new();

    [Fact]
    public void An_unknown_name_reads_as_unknown()
    {
        _variables.TryRead("missing", out var value).ShouldBeFalse();
        value.Presence.ShouldBe(BpmnValuePresence.Absent);
    }

    [Fact]
    public void An_explicit_null_is_known_and_null()
    {
        _variables.SetNull("nothing");

        _variables.TryRead("nothing", out var value).ShouldBeTrue();
        value.Presence.ShouldBe(BpmnValuePresence.Null);
    }

    [Fact]
    public void An_externally_stored_value_is_known_and_unreadable()
    {
        _variables.SetStoredExternally("blob", "application/pdf");

        _variables.TryRead("blob", out var value).ShouldBeTrue();
        value.Presence.ShouldBe(BpmnValuePresence.StoredExternally);
        value.HasValue.ShouldBeFalse();
        value.TypeHint.ShouldBe("application/pdf");
    }

    [Fact]
    public void Values_round_trip_through_the_typed_setters()
    {
        _variables.SetInteger("count", 42).SetCollection("names", new[] { "a", "b" }).SetValue("flag", true);

        _variables["count"]!.Json!.Value.GetInt32().ShouldBe(42);
        _variables["names"]!.Json!.Value.EnumerateArray().Select(item => item.GetString()).ShouldBe(["a", "b"]);
        _variables["flag"]!.Json!.Value.GetBoolean().ShouldBeTrue();
        _variables.Names.ShouldBe(["count", "flag", "names"]);
    }

    [Fact]
    public void Removing_a_variable_makes_it_unknown_again()
    {
        _variables.SetInteger("count", 1);

        _variables.Remove("count").ShouldBeTrue();
        _variables.TryRead("count", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_clone_is_independent_of_its_source()
    {
        _variables.SetInteger("count", 1);
        var clone = _variables.Clone();

        clone.SetInteger("count", 2);

        _variables["count"]!.Json!.Value.GetInt32().ShouldBe(1);
        clone["count"]!.Json!.Value.GetInt32().ShouldBe(2);
    }

    [Fact]
    public void A_nested_scope_sees_its_parents_variables_plus_its_own_iteration_frame()
    {
        var (parent, body) = ProcessFixtures.NestedSubProcess();
        var host = new InMemoryBpmnHost(new InMemoryBpmnHostOptions
        {
            NestedProcesses = new Dictionary<string, BpmnProcessDefinition>(StringComparer.Ordinal) { ["sub"] = body }
        });

        var instance = host.Start(parent, variables: new InMemoryVariables().SetValue("tenant", "acme"));
        var child = instance.Children.ShouldHaveSingleItem();

        child.Variables.TryRead("tenant", out var tenant).ShouldBeTrue();
        tenant.Json!.Value.GetString().ShouldBe("acme");

        child.Variables.SetValue("tenant", "other");
        instance.Variables["tenant"]!.Json!.Value.GetString().ShouldBe("acme", "a nested scope's writes stay in that scope");
    }
}
