using Xunit;

namespace Humcp.App.UiTests;

/// <summary>
/// One application instance for the whole suite. Launching a second window while the first is up
/// would have two of them competing for the foreground, and the keyboard tests would type into
/// whichever won.
/// </summary>
[CollectionDefinition(Name)]
public class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "humcp application";
}
