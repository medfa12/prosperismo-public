#include "pch.h"

#include "ProsperismoHost.h"
#include "ProsperismoHostSupport.h"

#include <thread>

namespace winrt::Prosperismo {
namespace {

template <typename Promise>
void RejectCurrentException(Promise const &promise) noexcept {
  try {
    throw;
  } catch (std::exception const &error) {
    promise.Reject(error.what());
  } catch (...) {
    promise.Reject("The Prosperismo Windows host encountered an unknown error.");
  }
}

void EmitProcessEvent(
    winrt::Microsoft::ReactNative::ReactContext const &context,
    std::string phase,
    std::optional<uint32_t> exitCode = std::nullopt,
    std::string message = {}) noexcept {
  if (!context) {
    return;
  }
  winrt::Microsoft::ReactNative::JSValueObject event{{"phase", std::move(phase)}};
  if (exitCode) {
    event["exitCode"] = static_cast<double>(*exitCode);
  }
  if (!message.empty()) {
    event["message"] = std::move(message);
  }
  context.EmitJSEvent(L"RCTDeviceEventEmitter", L"ProsperismoHostProcess", std::move(event));
}

HWND FindProsperismoWindow() noexcept {
  struct Search {
    DWORD processId;
    HWND window;
  } search{GetCurrentProcessId(), nullptr};

  EnumWindows(
      [](HWND window, LPARAM parameter) noexcept -> BOOL {
        auto &candidate = *reinterpret_cast<Search *>(parameter);
        DWORD processId{};
        GetWindowThreadProcessId(window, &processId);
        if (processId == candidate.processId && IsWindowVisible(window) && GetWindow(window, GW_OWNER) == nullptr) {
          candidate.window = window;
          return FALSE;
        }
        return TRUE;
      },
      reinterpret_cast<LPARAM>(&search));
  return search.window;
}

} // namespace

void ProsperismoHost::ListDirectory(
    std::string path,
    winrt::Microsoft::ReactNative::ReactPromise<std::vector<ProsperismoDirectoryEntry>> &&promise) noexcept {
  try {
    std::vector<ProsperismoDirectoryEntry> result;
    for (auto const &entry : prosperismo::host::ListDirectory(path)) {
      result.push_back({entry.name, entry.path, entry.kind, entry.symbolicLink});
    }
    promise.Resolve(result);
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::ReadTextFile(
    std::string path,
    winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept {
  try {
    promise.Resolve(prosperismo::host::ReadTextFile(path));
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::ReadBinaryFile(
    std::string path,
    winrt::Microsoft::ReactNative::ReactPromise<std::vector<uint8_t>> &&promise) noexcept {
  try {
    promise.Resolve(prosperismo::host::ReadBinaryFile(path));
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::WriteTextFile(
    std::string path,
    std::string contents,
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  try {
    prosperismo::host::WriteTextFile(path, contents);
    promise.Resolve();
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::CanonicalizePath(
    std::string path,
    winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept {
  try {
    promise.Resolve(prosperismo::host::CanonicalizePath(path));
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::ChooseGameDirectories(
    winrt::Microsoft::ReactNative::ReactPromise<std::vector<std::string>> &&promise) noexcept {
  auto pickerPromise = promise;
  std::thread([pickerPromise]() noexcept {
    try {
      pickerPromise.Resolve(prosperismo::host::ChooseGameDirectories());
    } catch (...) {
      RejectCurrentException(pickerPromise);
    }
  }).detach();
}

void ProsperismoHost::LoadLauncherSettings(
    winrt::Microsoft::ReactNative::ReactPromise<std::optional<std::string>> &&promise) noexcept {
  try {
    promise.Resolve(prosperismo::host::LoadLauncherSettings());
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::SaveLauncherSettings(
    std::string json,
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  try {
    prosperismo::host::SaveLauncherSettings(json);
    promise.Resolve();
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::FindEmulator(
    winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept {
  try {
    auto executable = prosperismo::host::FindEmulator();
    promise.Resolve(executable ? prosperismo::host::WideToUtf8(executable->wstring()) : std::string{});
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::FileExists(
    std::string path,
    winrt::Microsoft::ReactNative::ReactPromise<bool> &&promise) noexcept {
  try {
    promise.Resolve(prosperismo::host::FileExists(path));
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::ResolveShellAssets(
    winrt::Microsoft::ReactNative::ReactPromise<ProsperismoShellAssetPaths> &&promise) noexcept {
  try {
    auto paths = prosperismo::host::ResolveShellAssets();
    promise.Resolve({
        paths.oracleRoot,
        paths.firmwareRoot,
        paths.ui3Rco,
        paths.baseRco,
        paths.bgLayerRco,
        paths.npxs40087Eboot,
        paths.particle0Gnf,
        paths.particle1Gnf,
        paths.homeSource,
        paths.settingsIcon,
        paths.libraryIcon,
        paths.desktopIcon,
        paths.searchIcon,
        paths.genericGameIcon,
        paths.focusNoise,
        paths.nativeDrawCache,
        paths.nativeSequenceDirectory,
        paths.coldBootChime,
        paths.homeBgm,
    });
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::PlayAt9(
    std::string path,
    bool loop,
    double gain,
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  try {
    prosperismo::host::PlayAt9(path, loop, static_cast<float>(gain));
    promise.Resolve();
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::StopAt9(
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  prosperismo::host::StopAt9();
  promise.Resolve();
}

void ProsperismoHost::GetStartupRoute(
    winrt::Microsoft::ReactNative::ReactPromise<std::string> &&promise) noexcept {
  int argumentCount = 0;
  auto arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
  if (!arguments) {
    promise.Resolve("desktop");
    return;
  }
  bool bigPicture = false;
  for (int index = 1; index < argumentCount; ++index) {
    if (_wcsicmp(arguments[index], L"--big-picture") == 0 ||
        _wcsicmp(arguments[index], L"-bigpicture") == 0) {
      bigPicture = true;
      break;
    }
  }
  LocalFree(arguments);
  promise.Resolve(bigPicture ? "big-picture" : "desktop");
}

void ProsperismoHost::SetBigPictureMode(
    bool enabled,
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  auto modePromise = promise;
  auto uiDispatcher = m_context.UIDispatcher();
  if (!uiDispatcher) {
    modePromise.Reject("Prosperismo could not access the application UI dispatcher.");
    return;
  }

  // AppWindow presenters own the Win32 island that hosts React Native. Changing
  // presenters from the native-module thread can leave that island detached
  // (a responsive, but completely white, client area). Marshal the transition
  // to the same UI dispatcher that owns the React view.
  uiDispatcher.Post([enabled, modePromise]() noexcept {
    try {
      auto window = FindProsperismoWindow();
      if (!window) {
        throw std::runtime_error("Prosperismo could not find its application window.");
      }
      auto windowId = winrt::Microsoft::UI::GetWindowIdFromWindow(window);
      auto appWindow = winrt::Microsoft::UI::Windowing::AppWindow::GetFromWindowId(windowId);
      appWindow.SetPresenter(
          enabled
              ? winrt::Microsoft::UI::Windowing::AppWindowPresenterKind::FullScreen
              : winrt::Microsoft::UI::Windowing::AppWindowPresenterKind::Default);
      modePromise.Resolve();
    } catch (...) {
      RejectCurrentException(modePromise);
    }
  });
}

void ProsperismoHost::OpenPath(
    std::string path,
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  try {
    prosperismo::host::OpenPath(path);
    promise.Resolve();
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::RemoveDirectories(
    std::vector<std::string> paths,
    std::string titleId,
    bool confirmed,
    winrt::Microsoft::ReactNative::ReactPromise<std::vector<std::string>> &&promise) noexcept {
  try {
    promise.Resolve(prosperismo::host::RemoveSaveDataDirectories(paths, titleId, confirmed));
  } catch (...) {
    RejectCurrentException(promise);
  }
}

void ProsperismoHost::Launch(
    std::string executable,
    std::vector<std::string> arguments,
    std::string workingDirectory,
    winrt::Microsoft::ReactNative::ReactPromise<void> &&promise) noexcept {
  try {
    auto context = m_context;
    prosperismo::host::LaunchDetached(
        executable,
        arguments,
        workingDirectory,
        [context](std::optional<uint32_t> exitCode, std::string message) noexcept {
          if (exitCode) {
            EmitProcessEvent(context, "exited", exitCode);
          } else {
            EmitProcessEvent(context, "failed", std::nullopt, std::move(message));
          }
        });
    EmitProcessEvent(context, "running");
    promise.Resolve();
  } catch (...) {
    EmitProcessEvent(m_context, "failed", std::nullopt, "Windows rejected the emulator process launch.");
    RejectCurrentException(promise);
  }
}

} // namespace winrt::Prosperismo
