from pathlib import Path

# Loaded by dmgbuild with -D app=/path/to/Murmur.app and -D assets=/path/to/app/tools.
files = [(defines["app"], "Murmur.app")]
symlinks = {"Applications": "/Applications"}
# Finder flags on the app bundle would invalidate its code signature.

background = str(Path(defines["assets"]) / "dmg-background.png")
window_rect = ((120, 120), (640, 420))
icon_size = 96
icon_locations = {"Murmur.app": (158, 193), "Applications": (482, 193)}
