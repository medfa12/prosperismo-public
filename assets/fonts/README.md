# Prosperismo UI fonts

Prosperismo ships one UI family for both Avalonia presentation modes:

| Shell role | Bundled face | Weight |
| --- | --- | ---: |
| Light/default labels and titles | `FiraSans-Light.ttf` | 300 |
| Regular/body text | `FiraSans-Regular.ttf` | 400 |
| Medium/utility text | `FiraSans-Medium.ttf` | 500 |
| Semibold/table headings | `FiraSans-SemiBold.ttf` | 600 |
| Bold/selected labels and actions | `FiraSans-Bold.ttf` | 700 |

The Avalonia resource URI is `avares://Prosperismo.Shell/Assets/Fonts#Fira Sans`.
The C# `Ps5FontLibrary` maps the shell's light/regular/medium/semibold/bold
tokens to these concrete faces. Desktop and Big Picture therefore use the
same bundled family on a clean installation. Avalonia may use its platform
glyph fallback for scripts not covered by the Latin files; Latin UI text does
not depend on installed system fonts.

The files are Fira Sans, copyright 2012–2015 The Mozilla Foundation and
Telefonica S.A., licensed under the SIL Open Font License 1.1. The complete
license and attribution are in [`FiraSans/OFL.txt`](FiraSans/OFL.txt).
The bundled Light cut is sourced from the official Google Fonts repository:
<https://github.com/google/fonts/tree/main/ofl/firasans>.

No proprietary console font files or firmware font directories are runtime
inputs to the shipped frontend. Firmware typography remains research evidence
only and is not packaged or loaded by Prosperismo.
