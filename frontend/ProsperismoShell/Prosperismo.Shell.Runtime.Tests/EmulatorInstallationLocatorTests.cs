// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class EmulatorInstallationLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "prosperismo-installation-locator-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReleasePackageUsesTheNativeBackendBesideTheAvaloniaApphost()
    {
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(package);
        var backend = CreateNativeBackend(package);

        var located = EmulatorInstallationLocator.Locate(package, configuredPath: null);

        Assert.Equal(Path.GetFullPath(backend), located);
    }

    [Fact]
    public void ExplicitBackendPathTakesPrecedenceOverAReleaseSibling()
    {
        var package = Path.Combine(_root, "package");
        var configured = Path.Combine(_root, "configured");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(configured);
        _ = CreateNativeBackend(package);
        var backend = CreateNativeBackend(configured);

        var located = EmulatorInstallationLocator.Locate(package, backend);

        Assert.Equal(Path.GetFullPath(backend), located);
    }

    [Fact]
    public void RepositoryWindowsBuildUsesTheAuthoritativeBuildDirectory()
    {
        var repo = Path.Combine(_root, "repo");
        var app = Path.Combine(repo, "artifacts", "bin", "Prosperismo.Shell.App", "Release", "net10.0", "win-x64");
        var native = Path.Combine(repo, "_Build", "windows");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(native);
        var backend = CreateNativeBackend(native);

        var located = EmulatorInstallationLocator.Locate(app, configuredPath: null);

        Assert.Equal(Path.GetFullPath(backend), located);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreateNativeBackend(string directory)
    {
        var name = OperatingSystem.IsWindows()
            ? "prosperismo_emulator.exe"
            : "prosperismo_emulator";
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
