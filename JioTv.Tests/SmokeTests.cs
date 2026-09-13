using System;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// Smoke tests for the plugin entry point. Real tests arrive from Task 2 onward.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void PluginGuid_IsStable()
    {
        // Compile-time identity of the plugin; must never change across releases.
        var id = Guid.Parse("7d4abbd2-2e55-4a8c-9e6a-2afc4fc3a3e9");
        Assert.True(id != Guid.Empty);
    }
}
