namespace Humcp.Schema;

/// <summary>
/// What an operation does to the world, in neutral terms (ROADMAP §6.1, hedge 4). MCP derives this
/// from <c>annotations</c>; OpenAPI will derive it from the HTTP verb, more reliably. Neither
/// vocabulary appears here, so neither has to be taught to the rest of the app.
/// </summary>
/// <remarks>
/// Every field is nullable on purpose: "the source did not say" is a different fact from "the
/// source said no", and MCP's annotations are explicitly untrusted server-supplied hints
/// (ROADMAP §4, pitfall 5). Never treat <see cref="IsDestructive"/> being <c>false</c> as proof
/// that an operation is safe.
/// </remarks>
public sealed record OperationSafety(bool? IsReadOnly, bool? IsDestructive, bool? IsIdempotent)
{
    /// <summary>Nothing was stated. The honest default.</summary>
    public static readonly OperationSafety Unstated = new(null, null, null);

    /// <summary>
    /// A word or two for the accessible name of a tree node, or <c>null</c> when there is nothing
    /// worth saying. Destructive is reported ahead of read-only because it is the one a user needs
    /// to hear before pressing Enter.
    /// </summary>
    public string? ShortDescription => this switch
    {
        { IsDestructive: true } => "destructive",
        { IsReadOnly: true } => "read-only",
        _ => null,
    };
}
