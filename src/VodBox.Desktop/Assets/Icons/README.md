# VodBox application icons

Application-owned assets live here. Only `linux/256/vodbox.png` is an Avalonia resource, so the window icon does not embed all export sizes. Packaging reads the other committed exports directly; CI does not regenerate artwork or install an image-processing runtime.

- `Source/original.jpg`: user-provided image, retained without modification.
- `Source/tile-extracted.png`: transparent extraction produced with built-in imagegen. Prompt: crop the green rounded-square tile; remove the external pale backdrop and shadow; preserve interior artwork/colors; transparent outer corners; no redesign or text. The export script trims its fringe using the recorded tile bounds and an antialiased rounded-square mask.
- `vodbox.png`: 1024×1024 transparent master, no macOS-specific outer padding.
- `vodbox.ico`: Windows executable icon, containing 16, 24, 32, 48, 64, 128 and 256px images. Referenced by `ApplicationIcon` in the desktop project.
- `vodbox-macos.png`: 1024×1024 preview of the macOS canvas. Artwork is 824×824, centered with 100 transparent pixels on every edge. This is our chosen optical sizing for the legacy ICNS bundle; the padding is not claimed as a universal Apple requirement for newer layered icons.
- `vodbox.icns`: macOS bundle icon, with standard pixel sizes through 1024. Copied into `Contents/Resources`, referenced by `CFBundleIconFile`, before bundle signing.
- `linux/<size>/vodbox.png`: 16–1024px exports. deb/rpm install into `/usr/share/icons/hicolor/<size>x<size>/apps/`; AppImage uses a 256px root icon. The desktop entry uses `Icon=vodbox`.

Regenerate the committed exports with Python + Pillow:

```sh
python3 build/generate-icons.py
```

The script uses recorded coordinates for `Source/tile-extracted.png`. For a new design, review and update the tile bounds/mask first.

References: [Windows icon sizes](https://learn.microsoft.com/en-us/windows/win32/menurc/about-icons), [Apple app icons](https://developer.apple.com/design/human-interface-guidelines/app-icons).
