#pragma once

#include "JSValue.h"
#include "NativeModules.h"

#include <optional>
#include <string>
#include <vector>

namespace winrt::Prosperismo {

REACT_STRUCT(ProsperismoDirectoryEntry)
struct ProsperismoDirectoryEntry {
  REACT_FIELD(name)
  std::string name;

  REACT_FIELD(path)
  std::string path;

  REACT_FIELD(kind)
  std::string kind;

  REACT_FIELD(symbolicLink)
  bool symbolicLink{false};
};

REACT_STRUCT(ProsperismoShellAssetPaths)
struct ProsperismoShellAssetPaths {
  REACT_FIELD(oracleRoot)
  std::string oracleRoot;
  REACT_FIELD(firmwareRoot)
  std::string firmwareRoot;
  REACT_FIELD(ui3Rco)
  std::string ui3Rco;
  REACT_FIELD(baseRco)
  std::string baseRco;
  REACT_FIELD(bgLayerRco)
  std::string bgLayerRco;
  REACT_FIELD(npxs40087Eboot)
  std::string npxs40087Eboot;
  REACT_FIELD(particle0Gnf)
  std::string particle0Gnf;
  REACT_FIELD(particle1Gnf)
  std::string particle1Gnf;
  REACT_FIELD(homeSource)
  std::string homeSource;
  REACT_FIELD(settingsIcon)
  std::string settingsIcon;
  REACT_FIELD(libraryIcon)
  std::string libraryIcon;
  REACT_FIELD(desktopIcon)
  std::string desktopIcon;
  REACT_FIELD(searchIcon)
  std::string searchIcon;
  REACT_FIELD(genericGameIcon)
  std::string genericGameIcon;
  REACT_FIELD(focusNoise)
  std::string focusNoise;
  REACT_FIELD(nativeDrawCache)
  std::string nativeDrawCache;
  REACT_FIELD(nativeSequenceDirectory)
  std::string nativeSequenceDirectory;
  REACT_FIELD(coldBootChime)
  std::string coldBootChime;
  REACT_FIELD(homeBgm)
  std::string homeBgm;
};

REACT_MODULE(ProsperismoHost, L"ProsperismoHost")
struct ProsperismoHost {
  REACT_INIT(Initialize)
  void Initialize(winrt::Microsoft::ReactNative::ReactContext const &context) noexcept {
    m_context = context;
  }

  REACT_METHOD(ListDirectory, L"listDirectory")
  void ListDirectory(
      std::string path,
      winrt::Microsoft::ReactNative::ReactPromise<std::vector<ProsperismoDirectoryEntry>> &&promise) noexcept;

  REACT_METHOD(ReadTextFile, L"readTextFile")
  void ReadTextFile(
      std::string path,
      winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept;

  REACT_METHOD(ReadBinaryFile, L"readBinaryFile")
  void ReadBinaryFile(
      std::string path,
      winrt::Microsoft::ReactNative::ReactPromise<std::vector<uint8_t>> &&promise) noexcept;

  REACT_METHOD(WriteTextFile, L"writeTextFile")
  void WriteTextFile(
      std::string path,
      std::string contents,
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

  REACT_METHOD(CanonicalizePath, L"canonicalizePath")
  void CanonicalizePath(
      std::string path,
      winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept;

  REACT_METHOD(ChooseGameDirectories, L"chooseGameDirectories")
  void ChooseGameDirectories(
      winrt::Microsoft::ReactNative::ReactPromise<std::vector<std::string>> &&promise) noexcept;

  REACT_METHOD(LoadLauncherSettings, L"loadLauncherSettings")
  void LoadLauncherSettings(
      winrt::Microsoft::ReactNative::ReactPromise<std::optional<std::string>> &&promise) noexcept;

  REACT_METHOD(SaveLauncherSettings, L"saveLauncherSettings")
  void SaveLauncherSettings(
      std::string json,
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

  REACT_METHOD(FindEmulator, L"findEmulator")
  void FindEmulator(
      winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept;

  REACT_METHOD(FileExists, L"fileExists")
  void FileExists(
      std::string path,
      winrt::Microsoft::ReactNative::ReactPromise<bool> &&promise) noexcept;

  REACT_METHOD(ResolveShellAssets, L"resolveShellAssets")
  void ResolveShellAssets(
      winrt::Microsoft::ReactNative::ReactPromise<ProsperismoShellAssetPaths> &&promise) noexcept;

  REACT_METHOD(PlayAt9, L"playAt9")
  void PlayAt9(
      std::string path,
      bool loop,
      double gain,
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

  REACT_METHOD(StopAt9, L"stopAt9")
  void StopAt9(
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

  REACT_METHOD(GetStartupRoute, L"getStartupRoute")
  void GetStartupRoute(
      winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept;

  REACT_METHOD(SetBigPictureMode, L"setBigPictureMode")
  void SetBigPictureMode(
      bool enabled,
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

  REACT_METHOD(OpenPath, L"openPath")
  void OpenPath(
      std::string path,
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

  REACT_METHOD(RemoveDirectories, L"removeDirectories")
  void RemoveDirectories(
      std::vector<std::string> paths,
      std::string titleId,
      bool confirmed,
      winrt::Microsoft::ReactNative::ReactPromise<std::vector<std::string>> &&promise) noexcept;

  REACT_METHOD(Launch, L"launch")
  void Launch(
      std::string executable,
      std::vector<std::string> arguments,
      std::string workingDirectory,
      winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept;

 private:
  winrt::Microsoft::ReactNative::ReactContext m_context{nullptr};
};

} // namespace winrt::Prosperismo
