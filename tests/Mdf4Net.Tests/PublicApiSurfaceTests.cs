using System;
using System.IO;
using AwesomeAssertions;
using PublicApiGenerator;
using Xunit;

namespace Mdf4Net.Tests;

/// <summary>
/// Public-API tracking: renders the public surface of Mdf4Net as C# declarations and compares it
/// with <c>tests/Mdf4Net.Tests/ApiApprovals/Mdf4Net.approved.txt</c>. Any change fails the test
/// and drops a <c>.received.txt</c> next to the approval; updating the approval is a deliberate act
/// in the same pull request. Baselines are taken from the generated file, never hand-written.
/// </summary>
/// <remarks>
/// On the net48 leg this approves the netstandard2.0 build. One approval covers both assets: the
/// sources are the same, so a divergence is a finding, not a reason to split the baseline.
/// </remarks>
public class PublicApiSurfaceTests
{
    private static readonly ApiGeneratorOptions RenderOptions = new()
    {
        // Assembly attributes carry the build's version, which differs between local, CI and release.
        IncludeAssemblyAttributes = false,
        TreatRecordsAsClasses = false,
        ExcludeAttributes = new[]
        {
            "System.Diagnostics.DebuggerNonUserCodeAttribute",
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
            "System.Runtime.CompilerServices.NullableAttribute",
            "System.Runtime.CompilerServices.NullableContextAttribute",
            "System.Runtime.CompilerServices.IsReadOnlyAttribute",
        },
    };

    [Fact]
    public void Public_Api_Surface_Matches_The_Approved_File()
    {
        string actual = typeof(Mdf4File).Assembly.GeneratePublicApi(RenderOptions);
        string directory = Path.Combine(FindRepositoryRoot(), "tests", "Mdf4Net.Tests", "ApiApprovals");
        string approvedPath = Path.Combine(directory, "Mdf4Net.approved.txt");
        string receivedPath = Path.Combine(directory, "Mdf4Net.received.txt");

        if (!File.Exists(approvedPath))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(receivedPath, actual);
            File.Exists(approvedPath).Should().BeTrue($"no approved API file at {approvedPath}; create it from the .received.txt this test just wrote");
            return;
        }

        string approved = File.ReadAllText(approvedPath);
        if (!string.Equals(Normalize(approved), Normalize(actual), StringComparison.Ordinal))
        {
            File.WriteAllText(receivedPath, actual);
            Normalize(approved).Should().Be(Normalize(actual), "the public API changed: review Mdf4Net.received.txt against the approved file and update it in the same pull request if the change is intended");
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mdf4Net.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (Mdf4Net.sln).");
    }
}
