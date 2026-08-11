#include "pch.h"
#include "ShellTypography.h"

#include <dwrite.h>

namespace winrt::Prosperismo {
namespace {

bool HasFontFamily(
    winrt::com_ptr<IDWriteFontCollection> const &collection,
    wchar_t const *familyName) noexcept {
  UINT32 index = 0;
  BOOL exists = FALSE;
  return collection
      && SUCCEEDED(collection->FindFamilyName(familyName, &index, &exists))
      && exists;
}

struct ResolvedFont {
  char const *family;
  char const *source;
  bool firaSansAvailable;
};

ResolvedFont ResolveFont() noexcept {
  winrt::com_ptr<IDWriteFactory> factory;
  HRESULT const factoryResult = DWriteCreateFactory(
      DWRITE_FACTORY_TYPE_SHARED,
      __uuidof(IDWriteFactory),
      reinterpret_cast<::IUnknown **>(factory.put_void()));
  if (FAILED(factoryResult)) {
    return {"Segoe UI", "system-fallback", false};
  }

  winrt::com_ptr<IDWriteFontCollection> collection;
  if (FAILED(factory->GetSystemFontCollection(collection.put(), FALSE))) {
    return {"Segoe UI", "system-fallback", false};
  }

  bool const hasFiraSans = HasFontFamily(collection, L"Fira Sans");
  if (hasFiraSans) {
    return {"Fira Sans", "open-installed", true};
  }
  if (HasFontFamily(collection, L"Segoe UI Variable Text")) {
    return {"Segoe UI Variable Text", "system-variable", false};
  }
  return {"Segoe UI", "system-fallback", false};
}

} // namespace

void ShellTypography::GetConstants(
    winrt::Microsoft::ReactNative::ReactConstantProvider &provider) noexcept {
  ResolvedFont const font = ResolveFont();
  provider.Add(L"fontFamily", std::string{font.family});
  provider.Add(L"source", std::string{font.source});
  provider.Add(L"firaSansAvailable", font.firaSansAvailable);
}

} // namespace winrt::Prosperismo
