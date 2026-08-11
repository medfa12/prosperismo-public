#pragma once

#include <filesystem>
#include <functional>
#include <optional>
#include <string>
#include <vector>

namespace prosperismo::host {

struct DirectoryEntry {
  std::string name;
  std::string path;
  std::string kind;
  bool symbolicLink{false};
};

// Paths to user-local PS5 shell research inputs. Every field is empty when
// its source is unavailable; callers must retain their asset-free fallback.
// The resolver never copies, extracts, or modifies oracle content.
struct ShellAssetPaths {
  std::string oracleRoot;
  std::string firmwareRoot;
  std::string ui3Rco;
  std::string baseRco;
  std::string bgLayerRco;
  std::string npxs40087Eboot;
  std::string particle0Gnf;
  std::string particle1Gnf;
  std::string homeSource;
  std::string settingsIcon;
  std::string libraryIcon;
  std::string desktopIcon;
  std::string searchIcon;
  std::string genericGameIcon;
  std::string focusNoise;
  std::string nativeDrawCache;
  std::string nativeSequenceDirectory;
  std::string coldBootChime;
  std::string homeBgm;
};

// The shell uses one audio owner: a title snd0 replaces the ambient bed, and
// returning to a generic card restores the bed. Missing/invalid AT9 is a no-op.
void PlayAt9(std::string const &path, bool loop, float gain);
void StopAt9() noexcept;

std::wstring Utf8ToWide(std::string const &value);
std::string WideToUtf8(std::wstring const &value);

std::vector<DirectoryEntry> ListDirectory(std::string const &path);
std::string ReadTextFile(std::string const &path);
std::vector<uint8_t> ReadBinaryFile(std::string const &path);
void WriteTextFile(std::string const &path, std::string const &contents);
std::string CanonicalizePath(std::string const &path);
bool FileExists(std::string const &path);
ShellAssetPaths ResolveShellAssets();
void OpenPath(std::string const &path);
std::vector<std::string> RemoveSaveDataDirectories(
    std::vector<std::string> const &paths,
    std::string const &titleId,
    bool confirmed);

std::filesystem::path LauncherSettingsPath();
std::optional<std::string> LoadLauncherSettings();
void SaveLauncherSettings(std::string const &json);

std::optional<std::filesystem::path> FindEmulator();

// Implements the CommandLineToArgvW quoting contract used by CreateProcessW.
std::wstring QuoteWindowsArgument(std::wstring const &argument);
std::wstring BuildCommandLine(
    std::filesystem::path const &executable,
    std::vector<std::string> const &arguments);

// Resolves after CreateProcessW has accepted the process. A detached watcher
// retains and closes the process handle when the emulator exits.
void LaunchDetached(
    std::string const &executable,
    std::vector<std::string> const &arguments,
    std::string const &workingDirectory,
    std::function<void(std::optional<uint32_t>, std::string)> onExit = {});

std::vector<std::string> ChooseGameDirectories();

} // namespace prosperismo::host
