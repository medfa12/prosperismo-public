#define NOMINMAX 1
#define WIN32_LEAN_AND_MEAN 1
#include <windows.h>
#include <shellapi.h>

#include "../ProsperismoHostSupport.h"

#include <cassert>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

class ScopedEnvironmentVariable {
public:
  explicit ScopedEnvironmentVariable(wchar_t const *name) : name_(name) {
    auto required = GetEnvironmentVariableW(name_.c_str(), nullptr, 0);
    if (required != 0) {
      std::wstring value(required, L'\0');
      auto length = GetEnvironmentVariableW(name_.c_str(), value.data(), required);
      assert(length < required);
      value.resize(length);
      original_ = std::move(value);
    }
  }

  ~ScopedEnvironmentVariable() {
    auto value = original_ ? original_->c_str() : nullptr;
    auto restored = SetEnvironmentVariableW(name_.c_str(), value);
    assert(restored);
  }

  void Set(std::filesystem::path const &value) {
    auto configured = SetEnvironmentVariableW(name_.c_str(), value.c_str());
    assert(configured);
  }

private:
  std::wstring name_;
  std::optional<std::wstring> original_;
};

void VerifyCommandLineRoundTrip(std::vector<std::string> const &arguments) {
  auto commandLine = prosperismo::host::BuildCommandLine(LR"(C:\Program Files\Prosperismo\prosperismo_emulator.exe)", arguments);
  int count = 0;
  wchar_t **parsed = CommandLineToArgvW(commandLine.c_str(), &count);
  assert(parsed != nullptr);
  assert(count == static_cast<int>(arguments.size() + 1));
  assert(std::wstring{parsed[0]} == LR"(C:\Program Files\Prosperismo\prosperismo_emulator.exe)");
  for (size_t index = 0; index < arguments.size(); ++index) {
    assert(prosperismo::host::WideToUtf8(parsed[index + 1]) == arguments[index]);
  }
  LocalFree(parsed);
}

} // namespace

int main() {
  assert(prosperismo::host::QuoteWindowsArgument(L"") == L"\"\"");
  assert(prosperismo::host::QuoteWindowsArgument(L"plain") == L"plain");
  assert(prosperismo::host::QuoteWindowsArgument(L"two words") == L"\"two words\"");
  assert(prosperismo::host::QuoteWindowsArgument(L"say\"hi") == L"\"say\\\"hi\"");
  auto japanese = std::string{reinterpret_cast<char const *>(u8"日本語")};
  VerifyCommandLineRoundTrip({
      "--file=eboot.bin",
      R"(C:\Games\A title\eboot.bin)",
      R"(ends with slash \)",
      R"(embedded\"quote)",
      "",
      japanese,
  });

  auto utf8 = std::string{reinterpret_cast<char const *>(u8"Prosperismo 日本語")};
  assert(prosperismo::host::WideToUtf8(prosperismo::host::Utf8ToWide(utf8)) == utf8);

  auto temporary = std::filesystem::temp_directory_path() /
      (L"prosperismo-host-test-" + std::to_wstring(GetCurrentProcessId()));
  std::filesystem::create_directories(temporary / L"child");
  auto textPath = temporary / L"param.json";
  prosperismo::host::WriteTextFile(
      prosperismo::host::WideToUtf8(textPath.wstring()), "{\"title\":\"Prosperismo\"}");
  auto entries = prosperismo::host::ListDirectory(prosperismo::host::WideToUtf8(temporary.wstring()));
  assert(entries.size() == 2);
  assert(prosperismo::host::FileExists(prosperismo::host::WideToUtf8(textPath.wstring())));
  assert(prosperismo::host::ReadTextFile(prosperismo::host::WideToUtf8(textPath.wstring())) ==
      "{\"title\":\"Prosperismo\"}");
  auto binary = prosperismo::host::ReadBinaryFile(prosperismo::host::WideToUtf8(textPath.wstring()));
  auto binaryText = std::string{binary.begin(), binary.end()};
  assert(binaryText == "{\"title\":\"Prosperismo\"}");
  assert(!prosperismo::host::CanonicalizePath(prosperismo::host::WideToUtf8(textPath.wstring())).empty());

  auto saveData = temporary / L"_SaveData" / L"PPSATEST";
  std::filesystem::create_directories(saveData);
  prosperismo::host::WriteTextFile(
      prosperismo::host::WideToUtf8((saveData / L"slot.bin").wstring()), "save");
  auto failed = prosperismo::host::RemoveSaveDataDirectories(
      {prosperismo::host::WideToUtf8(saveData.wstring())}, "PPSATEST", true);
  assert(failed.empty());
  assert(!std::filesystem::exists(saveData));
  assert(prosperismo::host::RemoveSaveDataDirectories(
      {prosperismo::host::WideToUtf8(temporary.wstring())}, "PPSATEST", true).size() == 1);

  auto shellAudio = temporary / L"shell-audio";
  auto directAudio = temporary / L"direct-audio";
  std::filesystem::create_directories(shellAudio);
  std::filesystem::create_directories(directAudio);
  auto directoryChime = shellAudio / L"sfx_coldboot.at9";
  auto directoryBgm = shellAudio / L"bgm_home.at9";
  auto directChime = directAudio / L"boot.at9";
  auto directBgm = directAudio / L"home.at9";
  prosperismo::host::WriteTextFile(
      prosperismo::host::WideToUtf8(directoryChime.wstring()), "directory chime");
  prosperismo::host::WriteTextFile(
      prosperismo::host::WideToUtf8(directoryBgm.wstring()), "directory bgm");
  prosperismo::host::WriteTextFile(
      prosperismo::host::WideToUtf8(directChime.wstring()), "direct chime");
  prosperismo::host::WriteTextFile(
      prosperismo::host::WideToUtf8(directBgm.wstring()), "direct bgm");
  {
    ScopedEnvironmentVariable audioDirectory(L"PROSPERISMO_PS5_SHELL_AUDIO_DIR");
    ScopedEnvironmentVariable coldBootAudio(L"PROSPERISMO_PS5_COLD_BOOT_AUDIO");
    ScopedEnvironmentVariable homeBgm(L"PROSPERISMO_PS5_HOME_BGM");
    audioDirectory.Set(shellAudio);
    coldBootAudio.Set(directChime);
    homeBgm.Set(directBgm);
    auto shellAssets = prosperismo::host::ResolveShellAssets();
    assert(shellAssets.coldBootChime == prosperismo::host::WideToUtf8(
        std::filesystem::absolute(directChime).lexically_normal().wstring()));
    assert(shellAssets.homeBgm == prosperismo::host::WideToUtf8(
        std::filesystem::absolute(directBgm).lexically_normal().wstring()));
  }

  auto realSaveParent = temporary / L"real" / L"_SaveData";
  auto linkedSaveParent = temporary / L"linked" / L"_SaveData";
  std::filesystem::create_directories(realSaveParent / L"PPSALINK");
  std::filesystem::create_directories(linkedSaveParent.parent_path());
  std::error_code linkError;
  std::filesystem::create_directory_symlink(realSaveParent, linkedSaveParent, linkError);
  if (!linkError) {
    auto linkedTarget = linkedSaveParent / L"PPSALINK";
    auto linkedFailures = prosperismo::host::RemoveSaveDataDirectories(
        {prosperismo::host::WideToUtf8(linkedTarget.wstring())}, "PPSALINK", true);
    assert(linkedFailures.size() == 1);
    assert(std::filesystem::exists(realSaveParent / L"PPSALINK"));
  }
  bool rejectedUnconfirmed = false;
  try {
    prosperismo::host::RemoveSaveDataDirectories({}, "PPSATEST", false);
  } catch (std::invalid_argument const &) {
    rejectedUnconfirmed = true;
  }
  assert(rejectedUnconfirmed);
  std::filesystem::remove_all(temporary);
  return 0;
}
