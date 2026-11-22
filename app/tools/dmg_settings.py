# Loaded by dmgbuild with -D app=/path/to/Murmur.app.
files = [(defines["app"], "Murmur.app")]
symlinks = {"Applications": "/Applications"}
# Finder flags on the app bundle would invalidate its code signature.

background = "builtin-arrow"
window_rect = ((120, 120), (640, 340))
icon_size = 96
icon_locations = {"Murmur.app": (140, 120), "Applications": (500, 120)}
