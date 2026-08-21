using Humcp.Schema;

namespace Humcp.Schema.Tests;

public class OperationSafetyTests
{
    [Fact]
    public void Nothing_stated_says_nothing()
    {
        Assert.Null(OperationSafety.Unstated.ShortDescription);
    }

    [Fact]
    public void Destructive_is_reported_ahead_of_read_only()
    {
        // A tool claiming both is confused, and the one the user needs before pressing Enter wins.
        var safety = new OperationSafety(IsReadOnly: true, IsDestructive: true, IsIdempotent: null);

        Assert.Equal("destructive", safety.ShortDescription);
    }

    [Fact]
    public void Read_only_is_reported()
    {
        Assert.Equal("read-only", new OperationSafety(true, false, true).ShortDescription);
    }

    [Fact]
    public void Not_destructive_is_not_a_claim_worth_speaking()
    {
        // "Never treat destructiveHint: false as proof of safety" (ROADMAP §4, pitfall 5): saying
        // nothing is honest, saying "safe" would not be.
        Assert.Null(new OperationSafety(null, false, null).ShortDescription);
    }
}
