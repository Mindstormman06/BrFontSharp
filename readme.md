# BrFontSharp

NW4R font (`.brfnt`, and archived `.brfna`) reading and writing, with glyph lookup and sheet decoding through
TexSharp.

```csharp
BrfntFile font = BrfntFile.FromFile(@"…\romfs\US\Font\en_US\normal_00.brfnt");

Glyph glyph = font.GetGlyph('A')!;                      // index, widths, sheet and pixel position of its cell
byte[] sheet = font.Sheets.SheetToRgba8(glyph.Sheet);   // the whole sheet as RGBA8
byte[] packed = font.Write();
```

A font is its info (`FINF`), glyph sheets (`TGLP`), width blocks (`CWDH`) and code maps (`CMAP`: direct, table or
scan). Cells sit `CellWidth + 1` by `CellHeight + 1` apart on each sheet. Archived fonts (`RFNA`) keep their glyph
groups (`GLGR`) as bytes and store each sheet Huffman-compressed behind its length; `SheetData` and `SheetToRgba8`
decompress it.

All six distinct fonts in the *Skyward Sword HD* dump rebuild to the same bytes, every code maps inside the sheets,
and text drawn from each looks right.

## Build

```bash
git submodule update --init --recursive
dotnet build BrFontSharp.slnx -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
