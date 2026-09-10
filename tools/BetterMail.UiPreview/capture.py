"""Capture real X11 pixels, including native web views; Python standard library only."""
import ctypes as c
import struct
import sys
import zlib
from pathlib import Path

class XImage(c.Structure):
    _fields_ = [("width", c.c_int), ("height", c.c_int), ("xoffset", c.c_int),
                ("format", c.c_int), ("data", c.c_void_p), ("byte_order", c.c_int),
                ("bitmap_unit", c.c_int), ("bitmap_bit_order", c.c_int),
                ("bitmap_pad", c.c_int), ("depth", c.c_int), ("bytes_per_line", c.c_int),
                ("bits_per_pixel", c.c_int), ("red_mask", c.c_ulong),
                ("green_mask", c.c_ulong), ("blue_mask", c.c_ulong)]
x = c.CDLL("libX11.so.6")
x.XOpenDisplay.restype = c.c_void_p
x.XDefaultRootWindow.argtypes = [c.c_void_p]
x.XDefaultRootWindow.restype = c.c_ulong
x.XGetImage.argtypes = [c.c_void_p, c.c_ulong, c.c_int, c.c_int, c.c_uint, c.c_uint, c.c_ulong, c.c_int]
x.XGetImage.restype = c.POINTER(XImage)
x.XDestroyImage.argtypes = [c.POINTER(XImage)]
x.XCloseDisplay.argtypes = [c.c_void_p]
d = x.XOpenDisplay(None)
if not d:
    raise RuntimeError("Run the preview under xvfb-run.")
w, h = map(int, sys.argv[2:4])
im = x.XGetImage(d, x.XDefaultRootWindow(d), 0, 0, w, h, c.c_ulong(-1), 2)
try:
    info = im.contents
    assert info.bits_per_pixel == 32 and info.byte_order == 0 and info.red_mask == 0xff0000
    data = c.string_at(info.data, info.bytes_per_line * h)
    pixels = bytearray()
    for row in range(h):
        pixels.append(0)
        line = data[row * info.bytes_per_line:row * info.bytes_per_line + w * 4]
        for i in range(0, len(line), 4):
            pixels.extend((line[i + 2], line[i + 1], line[i]))
    def chunk(kind, content):
        return struct.pack('!I', len(content)) + kind + content + struct.pack('!I', zlib.crc32(kind + content))
    Path(sys.argv[1]).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!2I5B', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b''))
finally:
    x.XDestroyImage(im)
    x.XCloseDisplay(d)
