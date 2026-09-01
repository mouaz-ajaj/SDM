# Branding assets

`sdm-mark.png` and `sdm.ico` are both generated from `Logo.png` in the repository root,
which is the source artwork. Regenerate them from it rather than editing them by hand.

- **sdm-mark.png** — 512×512, used for `Window.Icon` and the sidebar wordmark.
- **sdm.ico** — 16, 24, 32, 48, 64, 128 and 256 px, set as `ApplicationIcon` so Explorer,
  the taskbar and Alt-Tab each choose a size instead of downscaling one bitmap and
  smearing the thin speed lines.

Both are cropped to the artwork and centred on a square with a small margin: the source
canvas is 42% empty vertically, which at 16 px leaves the mark too small to read.

The mark must keep a real alpha channel. An earlier version of `sdm-mark.png` was saved as
24-bit RGB with an editor's transparency checkerboard flattened into the pixels, which drew
a pale chequered square behind the logo on SDM's dark sidebar.
