using System.Xml;

namespace Bpmn.Runtime.InMemory;

/// <summary>
/// ISO-8601 duration parsing for timer work — <c>PT30M</c>, <c>PT1H</c>, <c>P7D</c>, <c>P1DT2H30M</c>.
/// <para>
/// This library carries no external dependencies, so the parsing is the base class library's
/// <see cref="XmlConvert.ToTimeSpan(string)"/> rather than a hand-rolled state machine: it is the same
/// lexical space (<c>xs:duration</c>), it is already tested by the platform, and reimplementing it would be a
/// second thing to get wrong. What this type adds is a failure mode worth reading — the platform reports a
/// bare "input string was not in a correct format", which tells you nothing about which timer is wrong.
/// </para>
/// <para>
/// One inherited caveat: <c>xs:duration</c> months and years are not exact. <see cref="XmlConvert"/> resolves
/// a month to 30 days and a year to 365. If that approximation matters to a model, express the timer in days
/// or hours instead.
/// </para>
/// </summary>
public static class IsoDuration
{
    /// <summary>
    /// Parses an ISO-8601 duration. Throws <see cref="FormatException"/> naming the offending text when it is
    /// not one.
    /// </summary>
    public static TimeSpan Parse(string text)
    {
        if (TryParse(text, out var value))
            return value;

        throw new FormatException(
            $"'{text}' is not an ISO-8601 duration. Expected something of the form PT30M, PT1H, P7D or P1DT2H30M "
            + "(a leading P, a T before any time component). Supply the duration through the host options instead "
            + "if the model does not carry one.");
    }

    /// <summary>Parses an ISO-8601 duration, reporting failure rather than throwing.</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            value = XmlConvert.ToTimeSpan(text.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
