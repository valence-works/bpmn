namespace Bpmn.Interchange;

/// <summary>
/// Thrown when a document cannot be read or written at all: malformed XML, a root element that is not
/// <c>&lt;definitions&gt;</c>, a document with no <c>&lt;process&gt;</c>, or an explicitly requested process
/// that the document does not declare. Everything the reader can recover from is reported as a
/// <see cref="BpmnImportIssue"/> instead.
/// </summary>
public sealed class BpmnInterchangeException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public BpmnInterchangeException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    public BpmnInterchangeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
