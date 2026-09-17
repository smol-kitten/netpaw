#!/usr/bin/env python3
"""Renders src/NetPaw.Tray/netpaw.ico: the same paw the tray draws at runtime (Icons.Paw), on a dark rounded
square so it reads in the taskbar, Explorer and Add/Remove Programs. Re-run after changing the geometry."""
from PIL import Image, ImageDraw
BG, PAW = (24, 26, 32, 255), (120, 170, 255, 255)          # Theme.Bg, Theme.Accent
def render(size):
    s = size * 4                                                # draw 4x, downsample for smooth edges
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    r = s * 0.22; d.rounded_rectangle((0, 0, s - 1, s - 1), radius=r, fill=BG)
    k = s / 32.0; pad = s * 0.06                                  # Icons.Paw geometry is on a 32 px grid; inset a little
    def ell(x, y, w, h): d.ellipse((pad + x * k * 0.88, pad + y * k * 0.88, pad + (x + w) * k * 0.88, pad + (y + h) * k * 0.88), fill=PAW)
    ell(7, 15, 18, 14); ell(3, 8, 7, 8); ell(10, 2, 6, 8); ell(17, 2, 6, 8); ell(23, 8, 7, 8)
    return im.resize((size, size), Image.LANCZOS)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [render(n) for n in sizes]
frames[-1].save("src/NetPaw.Tray/netpaw.ico", format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
print("wrote src/NetPaw.Tray/netpaw.ico", sizes)
