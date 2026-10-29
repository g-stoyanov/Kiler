using Kiler.Core;

namespace Kiler.App.Tests;

/// <summary>The account of a test (M4.3), in place of <see cref="WindowsAccount"/>: null when it cannot sign in.</summary>
internal sealed class FakeAccount(string? name) : ICurrentAccount
{
    public string? Name { get; } = name;
}
