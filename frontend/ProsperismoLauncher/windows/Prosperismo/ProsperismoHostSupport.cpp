#define NOMINMAX 1
#define WIN32_LEAN_AND_MEAN 1
#include <windows.h>

#include "ProsperismoHostSupport.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstring>
#include <fstream>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <system_error>
#include <thread>

#include <mmsystem.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shellapi.h>

namespace prosperismo::host {
namespace {

extern "C" {
#include "../../../../3rdparty/LibAtrac9/C/src/libatrac9.h"
}

std::mutex At9AudioMutex;
std::vector<uint8_t> At9AudioWav;
uint64_t At9AudioGeneration{};

uint16_t ReadU16(std::vector<uint8_t> const &bytes, size_t offset) {
  if (offset + 2 > bytes.size()) {
    throw std::invalid_argument("Truncated AT9 container.");
  }
  return static_cast<uint16_t>(bytes[offset] | (bytes[offset + 1] << 8));
}

uint32_t ReadU32(std::vector<uint8_t> const &bytes, size_t offset) {
  if (offset + 4 > bytes.size()) {
    throw std::invalid_argument("Truncated AT9 container.");
  }
  return static_cast<uint32_t>(bytes[offset]) |
      (static_cast<uint32_t>(bytes[offset + 1]) << 8) |
      (static_cast<uint32_t>(bytes[offset + 2]) << 16) |
      (static_cast<uint32_t>(bytes[offset + 3]) << 24);
}

void WriteU16(std::vector<uint8_t> &bytes, size_t offset, uint16_t value) {
  bytes[offset] = static_cast<uint8_t>(value);
  bytes[offset + 1] = static_cast<uint8_t>(value >> 8);
}

void WriteU32(std::vector<uint8_t> &bytes, size_t offset, uint32_t value) {
  for (int byte = 0; byte != 4; ++byte) {
    bytes[offset + byte] = static_cast<uint8_t>(value >> (byte * 8));
  }
}

std::vector<uint8_t> DecodeAt9ToWav(std::vector<uint8_t> const &file, float gain) {
  static constexpr uint8_t Atrac9Guid[] = {
      0xd2, 0x42, 0xe1, 0x47, 0xba, 0x36, 0x8d, 0x4d,
      0x88, 0xfc, 0x61, 0x65, 0x4f, 0x8c, 0x83, 0x6c};
  if (file.size() < 12 || std::memcmp(file.data(), "RIFF", 4) != 0 ||
      std::memcmp(file.data() + 8, "WAVE", 4) != 0) {
    throw std::invalid_argument("Audio is not a RIFF/WAVE AT9 stream.");
  }

  std::array<unsigned char, ATRAC9_CONFIG_DATA_SIZE> config{};
  bool haveConfig = false;
  uint32_t sampleCount{};
  uint32_t encoderDelay{};
  size_t dataOffset{};
  size_t dataBytes{};
  for (size_t offset = 12; offset + 8 <= file.size();) {
    auto const chunkBytes = static_cast<size_t>(ReadU32(file, offset + 4));
    auto const chunk = offset + 8;
    if (chunk > file.size() || chunkBytes > file.size() - chunk) {
      throw std::invalid_argument("AT9 contains a truncated RIFF chunk.");
    }
    if (std::memcmp(file.data() + offset, "fmt ", 4) == 0 && chunkBytes >= 48) {
      if (ReadU16(file, chunk) != 0xfffe ||
          std::memcmp(file.data() + chunk + 24, Atrac9Guid, sizeof(Atrac9Guid)) != 0) {
        throw std::invalid_argument("WAVE stream is not ATRAC9.");
      }
      std::copy_n(file.data() + chunk + 44, config.size(), config.begin());
      haveConfig = true;
    } else if (std::memcmp(file.data() + offset, "fact", 4) == 0 && chunkBytes >= 12) {
      sampleCount = ReadU32(file, chunk);
      encoderDelay = ReadU32(file, chunk + 8);
    } else if (std::memcmp(file.data() + offset, "data", 4) == 0) {
      dataOffset = chunk;
      dataBytes = chunkBytes;
    }
    offset = chunk + chunkBytes + (chunkBytes & 1u);
  }
  if (!haveConfig || sampleCount == 0 || dataOffset == 0) {
    throw std::invalid_argument("AT9 is missing fmt, fact, or data.");
  }

  struct DecoderGuard {
    void *value{Atrac9GetHandle()};
    ~DecoderGuard() { if (value) Atrac9ReleaseHandle(value); }
  } decoder;
  if (!decoder.value || Atrac9InitDecoder(decoder.value, config.data()) < 0) {
    throw std::runtime_error("LibAtrac9 rejected the stream configuration.");
  }
  Atrac9CodecInfo info{};
  if (Atrac9GetCodecInfo(decoder.value, &info) < 0 || info.channels <= 0 ||
      info.samplingRate <= 0 || info.superframeSize <= 0 || info.frameSamples <= 0 ||
      info.framesInSuperframe <= 0) {
    throw std::runtime_error("LibAtrac9 returned invalid stream information.");
  }

  auto const samplesPerSuperframe = info.frameSamples * info.framesInSuperframe;
  std::vector<int16_t> decoded(static_cast<size_t>(samplesPerSuperframe) * info.channels);
  std::vector<int16_t> pcm;
  pcm.reserve(static_cast<size_t>(sampleCount) * info.channels);
  uint64_t decodedFrames{};
  auto const end = dataOffset + dataBytes;
  for (size_t offset = dataOffset;
       offset + static_cast<size_t>(info.superframeSize) <= end && pcm.size() / info.channels < sampleCount;
       offset += static_cast<size_t>(info.superframeSize)) {
    int bytesUsed{};
    if (Atrac9Decode(decoder.value, file.data() + offset, decoded.data(), &bytesUsed, 0) < 0) {
      throw std::runtime_error("LibAtrac9 failed while decoding a superframe.");
    }
    for (int frame = 0; frame != samplesPerSuperframe && pcm.size() / info.channels < sampleCount; ++frame) {
      if (decodedFrames++ < encoderDelay) {
        continue;
      }
      auto const source = decoded.begin() + static_cast<ptrdiff_t>(frame * info.channels);
      pcm.insert(pcm.end(), source, source + info.channels);
    }
  }
  if (pcm.empty()) {
    throw std::runtime_error("AT9 decoder produced no PCM samples.");
  }

  auto const outputChannels = info.channels == 1 ? 1 : 2;
  auto const frames = pcm.size() / static_cast<size_t>(info.channels);
  auto const scale = std::isfinite(gain) && gain > 0.0f ? static_cast<double>(gain) : 1.0;
  std::vector<int16_t> output;
  output.reserve(frames * outputChannels);
  auto clampSample = [scale](double sample) {
    return static_cast<int16_t>(std::clamp<long>(
        std::lround(sample * scale), -32768, 32767));
  };
  for (size_t frame = 0; frame != frames; ++frame) {
    auto const source = pcm.data() + frame * info.channels;
    if (info.channels == 1) {
      output.push_back(clampSample(source[0]));
      continue;
    }
    double left = source[0];
    double right = source[1];
    if (info.channels > 2) {
      left += source[2] * 0.7071;
      right += source[2] * 0.7071;
      // WAVE order is FL, FR, C, LFE, then surround/height pairs. Leave LFE
      // out of a desktop stereo mix and fold each remaining pair to its side.
      for (int channel = 4; channel < info.channels; channel += 2) {
        left += source[channel] * 0.7071;
        if (channel + 1 < info.channels) {
          right += source[channel + 1] * 0.7071;
        }
      }
      left *= 0.5;
      right *= 0.5;
    }
    output.push_back(clampSample(left));
    output.push_back(clampSample(right));
  }

  auto const dataSize = static_cast<uint32_t>(output.size() * sizeof(int16_t));
  std::vector<uint8_t> wav(44 + dataSize);
  std::memcpy(wav.data(), "RIFF", 4);
  WriteU32(wav, 4, static_cast<uint32_t>(wav.size() - 8));
  std::memcpy(wav.data() + 8, "WAVEfmt ", 8);
  WriteU32(wav, 16, 16);
  WriteU16(wav, 20, 1);
  WriteU16(wav, 22, static_cast<uint16_t>(outputChannels));
  WriteU32(wav, 24, static_cast<uint32_t>(info.samplingRate));
  WriteU32(wav, 28, static_cast<uint32_t>(info.samplingRate * outputChannels * 2));
  WriteU16(wav, 32, static_cast<uint16_t>(outputChannels * 2));
  WriteU16(wav, 34, 16);
  std::memcpy(wav.data() + 36, "data", 4);
  WriteU32(wav, 40, dataSize);
  std::memcpy(wav.data() + 44, output.data(), dataSize);
  return wav;
}

std::runtime_error WindowsError(char const *operation, DWORD error = GetLastError()) {
  return std::runtime_error(std::string(operation) + " failed with Windows error " + std::to_string(error));
}

std::filesystem::path ExecutableDirectory() {
  std::wstring buffer(260, L'\0');
  for (;;) {
    DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0) {
      throw WindowsError("GetModuleFileNameW");
    }
    if (length < buffer.size()) {
      buffer.resize(length);
      return std::filesystem::path{buffer}.parent_path();
    }
    if (buffer.size() >= 32768) {
      throw std::runtime_error("The launcher executable path exceeds the Windows path limit.");
    }
    buffer.resize(std::min<size_t>(buffer.size() * 2, 32768));
  }
}

std::filesystem::path AbsoluteNormalized(std::filesystem::path const &path) {
  std::error_code error;
  auto absolute = std::filesystem::absolute(path, error);
  if (error) {
    throw std::filesystem::filesystem_error("Could not make path absolute", path, error);
  }
  return absolute.lexically_normal();
}

void AddCandidate(
    std::vector<std::filesystem::path> &candidates,
    std::filesystem::path const &candidate) {
  auto normalized = candidate.lexically_normal();
  if (std::find(candidates.begin(), candidates.end(), normalized) == candidates.end()) {
    candidates.push_back(std::move(normalized));
  }
}

std::optional<std::filesystem::path> EnvironmentPath(wchar_t const *name) {
  DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
  if (required == 0) {
    return std::nullopt;
  }
  std::wstring value(required, L'\0');
  DWORD length = GetEnvironmentVariableW(name, value.data(), required);
  if (length == 0 || length >= required) {
    return std::nullopt;
  }
  value.resize(length);
  return std::filesystem::path{value};
}

bool IsDirectory(std::filesystem::path const &path) noexcept {
  std::error_code error;
  return std::filesystem::is_directory(path, error) && !error;
}

bool IsFile(std::filesystem::path const &path) noexcept {
  std::error_code error;
  return std::filesystem::is_regular_file(path, error) && !error;
}

std::string ExistingFile(std::filesystem::path const &path) {
  return IsFile(path) ? WideToUtf8(AbsoluteNormalized(path).wstring()) : std::string{};
}

std::string ExistingDirectory(std::filesystem::path const &path) {
  return IsDirectory(path) ? WideToUtf8(AbsoluteNormalized(path).wstring()) : std::string{};
}

void AddOracleCandidatesFromBase(
    std::vector<std::filesystem::path> &candidates,
    std::filesystem::path base) {
  for (int depth = 0; depth != 10 && !base.empty(); ++depth) {
    AddCandidate(candidates, base / L"ps5oracle");
    // Development worktrees commonly sit beside the canonical Prosperismo
    // checkout rather than inside it (for example C:\prosperismo-ui).
    AddCandidate(candidates, base / L"prosperismo" / L"ps5oracle");
    auto parent = base.parent_path();
    if (parent == base) {
      break;
    }
    base = std::move(parent);
  }
}

std::optional<std::filesystem::path> LocateOracleRoot() {
  std::vector<std::filesystem::path> candidates;
  if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_ORACLE")) {
    AddCandidate(candidates, *configured);
  }

  std::error_code error;
  auto current = std::filesystem::current_path(error);
  if (!error) {
    AddOracleCandidatesFromBase(candidates, current);
  }
  AddOracleCandidatesFromBase(candidates, ExecutableDirectory());

  for (auto const &candidate : candidates) {
    if (IsDirectory(candidate / L"sony") && IsDirectory(candidate / L"evidence")) {
      return AbsoluteNormalized(candidate);
    }
  }
  return std::nullopt;
}

std::optional<std::filesystem::path> LocateFirmwareRoot(
    std::optional<std::filesystem::path> const &oracleRoot) {
  std::vector<std::filesystem::path> candidates;
  if (auto configured = EnvironmentPath(L"PROSPERISMO_FW_DUMP")) {
    AddCandidate(candidates, *configured);
  }
  if (oracleRoot) {
    AddCandidate(candidates, *oracleRoot / L"PS5_12.40");
    AddCandidate(candidates, *oracleRoot / L"sony" / L"PS5_12.40");
    AddCandidate(candidates, *oracleRoot / L"sony" / L"PS5_4.03_reconstructed");
    AddCandidate(candidates, *oracleRoot / L"sony" / L"300REC" / L"extracted");
  }

  for (auto const &candidate : candidates) {
    if (IsDirectory(candidate / L"filesystems" / L"system_ex" / L"vsh_asset")) {
      return AbsoluteNormalized(candidate);
    }
  }
  return std::nullopt;
}

void AddNativeSequenceCandidatesFromBase(
    std::vector<std::filesystem::path> &candidates,
    std::filesystem::path base) {
  for (int depth = 0; depth != 10 && !base.empty(); ++depth) {
    AddCandidate(candidates, base / L"out" /
        L"ps5-background-native-coldboot-ambient-runtime" / L"png");
    AddCandidate(candidates, base / L"prosperismo" / L"out" /
        L"ps5-background-native-coldboot-ambient-runtime" / L"png");
    auto parent = base.parent_path();
    if (parent == base) {
      break;
    }
    base = std::move(parent);
  }
}

std::string LocateNativeSequenceDirectory() {
  std::vector<std::filesystem::path> candidates;
  if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_BACKGROUND_FRAMES")) {
    AddCandidate(candidates, *configured);
  }
  std::error_code error;
  auto current = std::filesystem::current_path(error);
  if (!error) {
    AddNativeSequenceCandidatesFromBase(candidates, current);
  }
  AddNativeSequenceCandidatesFromBase(candidates, ExecutableDirectory());

  // The accepted sequence is 540 frames at 30 fps. Reject partial renders so
  // a failed cache build can never flash a truncated cold boot in the shell.
  for (auto const &candidate : candidates) {
    if (IsFile(candidate / L"00000.png") &&
        IsFile(candidate / L"00330.png") &&
        IsFile(candidate / L"00539.png")) {
      return ExistingDirectory(candidate);
    }
  }
  return {};
}

std::wstring ShellItemPath(IShellItem *item) {
  PWSTR rawPath = nullptr;
  auto result = item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
  if (FAILED(result)) {
    throw std::runtime_error("The selected shell item is not a filesystem directory.");
  }
  std::wstring path{rawPath};
  CoTaskMemFree(rawPath);
  return path;
}

} // namespace

std::wstring Utf8ToWide(std::string const &value) {
  if (value.empty()) {
    return {};
  }
  if (value.size() > static_cast<size_t>((std::numeric_limits<int>::max)())) {
    throw std::invalid_argument("UTF-8 input is too large.");
  }
  int required = MultiByteToWideChar(
      CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
  if (required == 0) {
    throw WindowsError("MultiByteToWideChar");
  }
  std::wstring result(static_cast<size_t>(required), L'\0');
  if (MultiByteToWideChar(
          CP_UTF8,
          MB_ERR_INVALID_CHARS,
          value.data(),
          static_cast<int>(value.size()),
          result.data(),
          required) == 0) {
    throw WindowsError("MultiByteToWideChar");
  }
  return result;
}

std::string WideToUtf8(std::wstring const &value) {
  if (value.empty()) {
    return {};
  }
  if (value.size() > static_cast<size_t>((std::numeric_limits<int>::max)())) {
    throw std::invalid_argument("UTF-16 input is too large.");
  }
  int required = WideCharToMultiByte(
      CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
  if (required == 0) {
    throw WindowsError("WideCharToMultiByte");
  }
  std::string result(static_cast<size_t>(required), '\0');
  if (WideCharToMultiByte(
          CP_UTF8,
          WC_ERR_INVALID_CHARS,
          value.data(),
          static_cast<int>(value.size()),
          result.data(),
          required,
          nullptr,
          nullptr) == 0) {
    throw WindowsError("WideCharToMultiByte");
  }
  return result;
}

std::vector<DirectoryEntry> ListDirectory(std::string const &path) {
  auto directory = std::filesystem::path{Utf8ToWide(path)};
  std::error_code error;
  if (!std::filesystem::is_directory(directory, error)) {
    if (error) {
      throw std::filesystem::filesystem_error("Could not inspect directory", directory, error);
    }
    throw std::invalid_argument("Path is not a directory: " + path);
  }

  std::vector<DirectoryEntry> result;
  std::filesystem::directory_iterator iterator{
      directory, std::filesystem::directory_options::skip_permission_denied, error};
  if (error) {
    throw std::filesystem::filesystem_error("Could not enumerate directory", directory, error);
  }
  for (auto end = std::filesystem::directory_iterator{}; iterator != end; iterator.increment(error)) {
    if (error) {
      throw std::filesystem::filesystem_error("Could not continue enumerating directory", directory, error);
    }
    auto linkStatus = iterator->symlink_status(error);
    if (error) {
      error.clear();
      continue;
    }
    auto followedStatus = iterator->status(error);
    if (error) {
      error.clear();
      continue;
    }
    bool isDirectory = std::filesystem::is_directory(followedStatus);
    bool isFile = std::filesystem::is_regular_file(followedStatus);
    if (!isDirectory && !isFile) {
      continue;
    }
    auto absolutePath = AbsoluteNormalized(iterator->path());
    result.push_back({
        WideToUtf8(iterator->path().filename().wstring()),
        WideToUtf8(absolutePath.wstring()),
        isDirectory ? "directory" : "file",
        std::filesystem::is_symlink(linkStatus),
    });
  }
  std::sort(result.begin(), result.end(), [](DirectoryEntry const &left, DirectoryEntry const &right) {
    return _stricmp(left.name.c_str(), right.name.c_str()) < 0;
  });
  return result;
}

std::string ReadTextFile(std::string const &path) {
  auto filePath = std::filesystem::path{Utf8ToWide(path)};
  std::ifstream stream{filePath, std::ios::binary};
  if (!stream) {
    throw std::runtime_error("Could not open file for reading: " + path);
  }
  std::string contents{std::istreambuf_iterator<char>{stream}, std::istreambuf_iterator<char>{}};
  if (!stream.eof() && stream.fail()) {
    throw std::runtime_error("Could not read file: " + path);
  }
  if (contents.size() >= 3 && static_cast<unsigned char>(contents[0]) == 0xef &&
      static_cast<unsigned char>(contents[1]) == 0xbb && static_cast<unsigned char>(contents[2]) == 0xbf) {
    contents.erase(0, 3);
  }
  // Reject malformed input so metadata parsing never gets replacement bytes.
  Utf8ToWide(contents);
  return contents;
}

std::vector<uint8_t> ReadBinaryFile(std::string const &path) {
  auto filePath = std::filesystem::path{Utf8ToWide(path)};
  std::ifstream stream{filePath, std::ios::binary | std::ios::ate};
  if (!stream) {
    throw std::runtime_error("Could not open file for reading: " + path);
  }
  auto length = stream.tellg();
  if (length < 0 || static_cast<uint64_t>(length) > static_cast<uint64_t>((std::numeric_limits<size_t>::max)())) {
    throw std::runtime_error("File is too large to read: " + path);
  }
  std::vector<uint8_t> contents(static_cast<size_t>(length));
  stream.seekg(0, std::ios::beg);
  if (!contents.empty()) {
    stream.read(reinterpret_cast<char *>(contents.data()), static_cast<std::streamsize>(contents.size()));
  }
  if (!stream) {
    throw std::runtime_error("Could not read file: " + path);
  }
  return contents;
}

void WriteTextFile(std::string const &path, std::string const &contents) {
  Utf8ToWide(contents);
  auto destination = AbsoluteNormalized(std::filesystem::path{Utf8ToWide(path)});
  if (destination.filename().empty()) {
    throw std::invalid_argument("A text-file destination must include a filename.");
  }
  std::error_code error;
  std::filesystem::create_directories(destination.parent_path(), error);
  if (error) {
    throw std::filesystem::filesystem_error("Could not create the destination directory", destination.parent_path(), error);
  }
  auto temporary = destination;
  temporary += L".tmp";
  {
    std::ofstream stream{temporary, std::ios::binary | std::ios::trunc};
    if (!stream) {
      throw std::runtime_error("Could not open temporary text file.");
    }
    stream.write(contents.data(), static_cast<std::streamsize>(contents.size()));
    stream.flush();
    if (!stream) {
      throw std::runtime_error("Could not write temporary text file.");
    }
  }
  if (!MoveFileExW(
          temporary.c_str(), destination.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
    auto failure = WindowsError("MoveFileExW");
    DeleteFileW(temporary.c_str());
    throw failure;
  }
}

std::string CanonicalizePath(std::string const &path) {
  auto input = std::filesystem::path{Utf8ToWide(path)};
  std::error_code error;
  auto canonical = std::filesystem::weakly_canonical(input, error);
  if (error) {
    canonical = AbsoluteNormalized(input);
  }
  return WideToUtf8(canonical.lexically_normal().wstring());
}

bool FileExists(std::string const &path) {
  std::error_code error;
  return std::filesystem::exists(std::filesystem::path{Utf8ToWide(path)}, error) && !error;
}

ShellAssetPaths ResolveShellAssets() {
  ShellAssetPaths result;
  auto oracleRoot = LocateOracleRoot();
  auto firmwareRoot = LocateFirmwareRoot(oracleRoot);

  if (oracleRoot) {
    result.oracleRoot = WideToUtf8(oracleRoot->wstring());
  }
  if (firmwareRoot) {
    result.firmwareRoot = WideToUtf8(firmwareRoot->wstring());

    auto vshAsset = *firmwareRoot / L"filesystems" / L"system_ex" / L"vsh_asset";
    result.ui3Rco = ExistingFile(vshAsset / L"Sce.PlayStation.PUI_UI3.rco");
    result.particle0Gnf = ExistingFile(vshAsset / L"Sce.Vsh.ShellUI.BGLayer.Particle0.gnf");
    result.particle1Gnf = ExistingFile(vshAsset / L"Sce.Vsh.ShellUI.BGLayer.Particle1.gnf");
    result.coldBootChime = ExistingFile(vshAsset / L"sfx_coldboot.at9");
    result.homeBgm = ExistingFile(vshAsset / L"bgm_home.at9");
    result.npxs40087Eboot = ExistingFile(
        *firmwareRoot / L"filesystems" / L"system_ex" / L"app" / L"NPXS40087" / L"eboot.bin");
  }

  // Keep restricted firmware media outside the checkout while allowing a
  // user to supply only the two shell tracks instead of a complete dump.
  // Existing direct-file overrides take precedence over the audio directory,
  // which in turn takes precedence over files discovered in the firmware.
  if (auto audioDirectory = EnvironmentPath(L"PROSPERISMO_PS5_SHELL_AUDIO_DIR")) {
    if (auto path = ExistingFile(*audioDirectory / L"sfx_coldboot.at9"); !path.empty()) {
      result.coldBootChime = std::move(path);
    }
    if (auto path = ExistingFile(*audioDirectory / L"bgm_home.at9"); !path.empty()) {
      result.homeBgm = std::move(path);
    }
  }
  if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_COLD_BOOT_AUDIO")) {
    if (auto path = ExistingFile(*configured); !path.empty()) {
      result.coldBootChime = std::move(path);
    }
  }
  if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_HOME_BGM")) {
    if (auto path = ExistingFile(*configured); !path.empty()) {
      result.homeBgm = std::move(path);
    }
  }

  std::filesystem::path resourceDirectory;
  if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_SHELL_RESOURCE_DIR")) {
    resourceDirectory = *configured;
  } else if (firmwareRoot) {
    resourceDirectory = *firmwareRoot / L"filesystems" / L"system_ex" / L"app" /
        L"NPXS40087" / L"psm" / L"Application" / L"resource";
  }
  if (!resourceDirectory.empty()) {
    result.baseRco = ExistingFile(resourceDirectory / L"Sce.Vsh.ShellUI.Base.rco");
    result.bgLayerRco = ExistingFile(resourceDirectory / L"Sce.Vsh.ShellUI.BGLayer.rco");
  }

  if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_HOME_SOURCE")) {
    result.homeSource = ExistingFile(*configured);
  }

  if (oracleRoot) {
    if (result.homeSource.empty()) {
      result.homeSource = ExistingFile(
          *oracleRoot / L"sony" / L"useful rnps" / L"readable_js_3.00" / L"NPXS40002.js");
    }
    auto runtimeIcons = *oracleRoot / L"evidence" / L"shell-icons-runtime" /
        L"Sce.PlayStation.PUI_UI3";
    result.settingsIcon = ExistingFile(runtimeIcons / L"emoji_settings.png");
    result.libraryIcon = ExistingFile(runtimeIcons / L"emoji_game_and_apps.png");
    result.desktopIcon = ExistingFile(runtimeIcons / L"emoji_system.png");
    result.searchIcon = ExistingFile(runtimeIcons / L"iconid_search.svg");
    result.genericGameIcon = ExistingFile(runtimeIcons / L"emoji_game.png");

    // Sony's image_focus_noise, extracted in place from PUI_UI3.rco by the
    // oracle's focus-noise evidence pass. Both focus passes sample it.
    result.focusNoise = ExistingFile(
        *oracleRoot / L"evidence" / L"shell-rendering" / L"focus-noise" /
        L"Sce.PlayStation.PUI_UI3" / L"image_focus_noise.png");

    std::filesystem::path drawCache;
    if (auto configured = EnvironmentPath(L"PROSPERISMO_PS5_NATIVE_DRAW_CACHE")) {
      drawCache = *configured;
    } else {
      drawCache = *oracleRoot / L"evidence" / L"shell-rendering" /
          L"native-small-bottom" / L"draw-cache";
    }
    result.nativeDrawCache = ExistingDirectory(drawCache);
  }

  result.nativeSequenceDirectory = LocateNativeSequenceDirectory();

  return result;
}

void OpenPath(std::string const &path) {
  auto target = AbsoluteNormalized(std::filesystem::path{Utf8ToWide(path)});
  std::error_code error;
  if (!std::filesystem::exists(target, error) || error) {
    throw std::invalid_argument("Path does not exist: " + path);
  }
  auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", target.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
  if (result <= 32) {
    throw std::runtime_error("Windows could not open the requested path (ShellExecute error " +
        std::to_string(static_cast<long long>(result)) + ").");
  }
}

std::vector<std::string> RemoveSaveDataDirectories(
    std::vector<std::string> const &paths,
    std::string const &titleId,
    bool confirmed) {
  if (!confirmed) {
    throw std::invalid_argument("Save-data deletion requires explicit confirmation.");
  }
  if (titleId.empty() || !std::all_of(titleId.begin(), titleId.end(), [](unsigned char character) {
        return std::isalnum(character) != 0 || character == '-' || character == '_';
      })) {
    throw std::invalid_argument("Save-data deletion requires a valid title id.");
  }

  auto expectedTitle = Utf8ToWide(titleId);
  std::vector<std::string> failed;
  for (auto const &path : paths) {
    try {
      auto input = std::filesystem::path{Utf8ToWide(path)};
      if (!input.is_absolute()) {
        throw std::invalid_argument("Save-data target must be absolute.");
      }
      auto target = AbsoluteNormalized(input);
      if (target == target.root_path() || target.filename().empty() ||
          _wcsicmp(target.filename().c_str(), expectedTitle.c_str()) != 0 ||
          _wcsicmp(target.parent_path().filename().c_str(), L"_SaveData") != 0) {
        throw std::invalid_argument("Save-data target is outside the exact _SaveData/title-id boundary.");
      }

      DWORD attributes = GetFileAttributesW(target.c_str());
      if (attributes == INVALID_FILE_ATTRIBUTES) {
        DWORD attributeError = GetLastError();
        if (attributeError == ERROR_FILE_NOT_FOUND || attributeError == ERROR_PATH_NOT_FOUND) {
          continue;
        }
        throw WindowsError("GetFileAttributesW", attributeError);
      }
      if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0 || (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
        throw std::invalid_argument("Save-data target must be a non-reparse directory.");
      }
      DWORD parentAttributes = GetFileAttributesW(target.parent_path().c_str());
      if (parentAttributes == INVALID_FILE_ATTRIBUTES ||
          (parentAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
          (parentAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
        throw std::invalid_argument("Save-data parent must be a non-reparse _SaveData directory.");
      }

      std::error_code error;
      std::filesystem::remove_all(target, error);
      if (error || std::filesystem::exists(target)) {
        throw std::filesystem::filesystem_error("Could not remove save-data directory", target, error);
      }
    } catch (...) {
      failed.push_back(path);
    }
  }
  return failed;
}

std::filesystem::path LauncherSettingsPath() {
  PWSTR localAppData = nullptr;
  auto result = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &localAppData);
  if (FAILED(result)) {
    throw std::runtime_error("Could not resolve the current user's LocalAppData directory.");
  }
  std::filesystem::path path{localAppData};
  CoTaskMemFree(localAppData);
  return path / L"Prosperismo" / L"launcher-settings.json";
}

std::optional<std::string> LoadLauncherSettings() {
  auto path = LauncherSettingsPath();
  std::error_code error;
  if (!std::filesystem::exists(path, error)) {
    if (error) {
      throw std::filesystem::filesystem_error("Could not inspect launcher settings", path, error);
    }
    return std::nullopt;
  }
  return ReadTextFile(WideToUtf8(path.wstring()));
}

void SaveLauncherSettings(std::string const &json) {
  auto path = LauncherSettingsPath();
  WriteTextFile(WideToUtf8(path.wstring()), json);
}

void PlayAt9(std::string const &path, bool loop, float gain) {
  if (path.empty() || !IsFile(std::filesystem::path{Utf8ToWide(path)})) {
    StopAt9();
    return;
  }

  uint64_t generation;
  {
    std::scoped_lock lock{At9AudioMutex};
    generation = ++At9AudioGeneration;
  }
  std::thread([path, loop, gain, generation]() noexcept {
    try {
      auto wav = DecodeAt9ToWav(ReadBinaryFile(path), gain);
      std::scoped_lock lock{At9AudioMutex};
      if (generation != At9AudioGeneration) {
        return;
      }
      PlaySoundW(nullptr, nullptr, 0);
      At9AudioWav = std::move(wav);
      auto flags = SND_MEMORY | SND_ASYNC | SND_NODEFAULT;
      if (loop) {
        flags |= SND_LOOP;
      }
      if (!PlaySoundW(
              reinterpret_cast<LPCWSTR>(At9AudioWav.data()),
              nullptr,
              flags)) {
        At9AudioWav.clear();
      }
    } catch (...) {
      // Firmware and title audio are optional. Corrupt or unsupported input
      // must never interfere with the launcher or emulator process.
    }
  }).detach();
}

void StopAt9() noexcept {
  std::scoped_lock lock{At9AudioMutex};
  ++At9AudioGeneration;
  PlaySoundW(nullptr, nullptr, 0);
  At9AudioWav.clear();
}

std::optional<std::filesystem::path> FindEmulator() {
  constexpr wchar_t emulatorName[] = L"prosperismo_emulator.exe";
  std::vector<std::filesystem::path> candidates;
  auto appDirectory = ExecutableDirectory();
  AddCandidate(candidates, appDirectory / emulatorName);
  AddCandidate(candidates, appDirectory.parent_path() / emulatorName);

  std::error_code error;
  auto current = std::filesystem::current_path(error);
  if (!error) {
    AddCandidate(candidates, current / emulatorName);
    AddCandidate(candidates, current / L"artifacts" / L"Prosperismo-Windows-x64" / emulatorName);
  }

  auto ancestor = appDirectory;
  for (int depth = 0; depth != 8 && !ancestor.empty(); ++depth) {
    AddCandidate(candidates, ancestor / L"artifacts" / L"Prosperismo-Windows-x64" / emulatorName);
    auto parent = ancestor.parent_path();
    if (parent == ancestor) {
      break;
    }
    ancestor = std::move(parent);
  }

  for (auto const &candidate : candidates) {
    if (std::filesystem::is_regular_file(candidate, error) && !error) {
      return AbsoluteNormalized(candidate);
    }
    error.clear();
  }
  return std::nullopt;
}

std::wstring QuoteWindowsArgument(std::wstring const &argument) {
  if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) {
    return argument;
  }

  std::wstring quoted{L'\"'};
  size_t backslashes = 0;
  for (wchar_t character : argument) {
    if (character == L'\\') {
      ++backslashes;
      continue;
    }
    if (character == L'\"') {
      quoted.append(backslashes * 2 + 1, L'\\');
      quoted.push_back(L'\"');
    } else {
      quoted.append(backslashes, L'\\');
      quoted.push_back(character);
    }
    backslashes = 0;
  }
  quoted.append(backslashes * 2, L'\\');
  quoted.push_back(L'\"');
  return quoted;
}

std::wstring BuildCommandLine(
    std::filesystem::path const &executable,
    std::vector<std::string> const &arguments) {
  std::wstring commandLine = QuoteWindowsArgument(executable.wstring());
  for (auto const &argument : arguments) {
    commandLine.push_back(L' ');
    commandLine.append(QuoteWindowsArgument(Utf8ToWide(argument)));
  }
  return commandLine;
}

void LaunchDetached(
    std::string const &executable,
    std::vector<std::string> const &arguments,
    std::string const &workingDirectory,
    std::function<void(std::optional<uint32_t>, std::string)> onExit) {
  auto executablePath = AbsoluteNormalized(std::filesystem::path{Utf8ToWide(executable)});
  std::error_code error;
  if (!std::filesystem::is_regular_file(executablePath, error) || error) {
    throw std::invalid_argument("Emulator executable does not exist: " + executable);
  }
  auto workingPath = AbsoluteNormalized(std::filesystem::path{Utf8ToWide(workingDirectory)});
  if (!std::filesystem::is_directory(workingPath, error) || error) {
    throw std::invalid_argument("Emulator working directory does not exist: " + workingDirectory);
  }

  auto commandLine = BuildCommandLine(executablePath, arguments);
  std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
  mutableCommandLine.push_back(L'\0');
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  if (!CreateProcessW(
          executablePath.c_str(),
          mutableCommandLine.data(),
          nullptr,
          nullptr,
          FALSE,
          CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS | CREATE_UNICODE_ENVIRONMENT,
          nullptr,
          workingPath.c_str(),
          &startup,
          &process)) {
    throw WindowsError("CreateProcessW");
  }
  CloseHandle(process.hThread);
  std::thread([handle = process.hProcess, onExit = std::move(onExit)]() noexcept {
    auto waitResult = WaitForSingleObject(handle, INFINITE);
    DWORD exitCode = 0;
    bool readExitCode = waitResult == WAIT_OBJECT_0 && GetExitCodeProcess(handle, &exitCode);
    CloseHandle(handle);
    if (onExit) {
      if (readExitCode) {
        onExit(static_cast<uint32_t>(exitCode), {});
      } else {
        onExit(std::nullopt, "Windows could not observe the emulator process exit.");
      }
    }
  }).detach();
}

std::vector<std::string> ChooseGameDirectories() {
  HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
  if (FAILED(initialized)) {
    throw std::runtime_error("Could not initialize the folder picker COM apartment.");
  }
  struct ApartmentGuard {
    ~ApartmentGuard() { CoUninitialize(); }
  } apartmentGuard;

  IFileOpenDialog *dialog = nullptr;
  HRESULT result = CoCreateInstance(
      CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dialog));
  if (FAILED(result)) {
    throw std::runtime_error("Could not create the Windows folder picker.");
  }
  struct DialogGuard {
    IFileOpenDialog *value;
    ~DialogGuard() { value->Release(); }
  } dialogGuard{dialog};

  FILEOPENDIALOGOPTIONS options{};
  result = dialog->GetOptions(&options);
  if (FAILED(result) || FAILED(dialog->SetOptions(
                            options | FOS_PICKFOLDERS | FOS_ALLOWMULTISELECT | FOS_FORCEFILESYSTEM |
                                FOS_PATHMUSTEXIST))) {
    throw std::runtime_error("Could not configure the Windows folder picker.");
  }
  dialog->SetTitle(L"Choose Prosperismo game folders");
  result = dialog->Show(nullptr);
  if (result == HRESULT_FROM_WIN32(ERROR_CANCELLED)) {
    return {};
  }
  if (FAILED(result)) {
    throw std::runtime_error("The Windows folder picker failed.");
  }

  IShellItemArray *items = nullptr;
  result = dialog->GetResults(&items);
  if (FAILED(result)) {
    throw std::runtime_error("Could not read the selected game folders.");
  }
  struct ItemsGuard {
    IShellItemArray *value;
    ~ItemsGuard() { value->Release(); }
  } itemsGuard{items};

  DWORD count = 0;
  if (FAILED(items->GetCount(&count))) {
    throw std::runtime_error("Could not count the selected game folders.");
  }
  std::vector<std::string> paths;
  paths.reserve(count);
  for (DWORD index = 0; index < count; ++index) {
    IShellItem *item = nullptr;
    if (FAILED(items->GetItemAt(index, &item))) {
      throw std::runtime_error("Could not inspect a selected game folder.");
    }
    struct ItemGuard {
      IShellItem *value;
      ~ItemGuard() { value->Release(); }
    } itemGuard{item};
    auto selected = std::filesystem::path{ShellItemPath(item)};
    paths.push_back(WideToUtf8(std::filesystem::weakly_canonical(selected).wstring()));
  }
  return paths;
}

} // namespace prosperismo::host
