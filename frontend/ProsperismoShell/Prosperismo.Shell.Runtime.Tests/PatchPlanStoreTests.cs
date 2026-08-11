using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class PatchPlanStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "prosperismo-patches-" + Guid.NewGuid());

    public PatchPlanStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ResolvesExactNormalizedPlanBesideEmulator()
    {
        var executable = CreateEmulator();
        var patchDirectory = Directory.CreateDirectory(Path.Combine(_root, "_Patches")).FullName;
        var expected = Path.Combine(patchDirectory, "PPSA01325.json");
        File.WriteAllText(expected, "{\"patches\":[]}");

        var resolved = PatchPlanStore.ResolveExistingPlan(executable, "  ppsa01325  ");

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ReturnsNullWhenTitleOrExactPlanIsAbsent()
    {
        var executable = CreateEmulator();
        Directory.CreateDirectory(Path.Combine(_root, "_Patches"));
        File.WriteAllText(Path.Combine(_root, "PPSA01325.json"), "{}");

        Assert.Null(PatchPlanStore.ResolveExistingPlan(executable, null));
        Assert.Null(PatchPlanStore.ResolveExistingPlan(executable, "  "));
        Assert.Null(PatchPlanStore.ResolveExistingPlan(executable, "PPSA01325"));
    }

    [Theory]
    [InlineData("PPSA0132")]
    [InlineData("PPSA013250")]
    [InlineData("PPSA/1325")]
    [InlineData("../PPSA01")]
    [InlineData("PPSA01A25")]
    [InlineData("123401325")]
    [InlineData("PPSA0132!")]
    public void RejectsMalformedTitleIdsWithoutPathGuessing(string titleId)
    {
        var executable = CreateEmulator();

        Assert.Throws<ArgumentException>(() =>
            PatchPlanStore.ResolveExistingPlan(executable, titleId));
    }

    [Fact]
    public void DoesNotGuessAlternateFoldersOrFilenames()
    {
        var executable = CreateEmulator();
        Directory.CreateDirectory(Path.Combine(_root, "patches"));
        File.WriteAllText(Path.Combine(_root, "patches", "PPSA01325.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "_Patches"));
        File.WriteAllText(Path.Combine(_root, "_Patches", "PPSA01325.patch.json"), "{}");

        Assert.Null(PatchPlanStore.ResolveExistingPlan(executable, "PPSA01325"));
    }

    [Fact]
    public void ValidatesAndCanonicalizesExistingJsonPlan()
    {
        var patchDirectory = Directory.CreateDirectory(Path.Combine(_root, "_Patches")).FullName;
        var patch = Path.Combine(patchDirectory, "PPSA01325.json");
        File.WriteAllText(patch, "{}");

        Assert.Equal(Path.GetFullPath(patch), PatchPlanStore.ValidateExistingPlanPath(patch));
        Assert.Throws<ArgumentException>(() =>
            PatchPlanStore.ValidateExistingPlanPath(Path.Combine(_root, "plan.txt")));
        Assert.Throws<FileNotFoundException>(() =>
            PatchPlanStore.ValidateExistingPlanPath(Path.Combine(_root, "missing.json")));
    }

    [Theory]
    [InlineData("PPSA01325", "PPSA01325")]
    [InlineData("ppsa01325", "PPSA01325")]
    [InlineData(" NPXS40087 ", "NPXS40087")]
    public void NormalizesSupportedTitleIdShape(string input, string expected)
    {
        Assert.Equal(expected, PatchPlanStore.NormalizeTitleId(input));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateEmulator()
    {
        var executable = Path.Combine(_root, "prosperismo_emulator");
        File.WriteAllText(executable, string.Empty);
        return executable;
    }
}
