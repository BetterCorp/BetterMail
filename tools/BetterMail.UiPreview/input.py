"""Send real X11 input to the offline preview for selection regression checks."""
import ctypes as c
import sys
x = c.CDLL('libX11.so.6')
t = c.CDLL('libXtst.so.6')
x.XOpenDisplay.restype = c.c_void_p
x.XStringToKeysym.argtypes = [c.c_char_p]
x.XStringToKeysym.restype = c.c_ulong
x.XKeysymToKeycode.argtypes = [c.c_void_p, c.c_ulong]
x.XKeysymToKeycode.restype = c.c_uint
x.XSync.argtypes = [c.c_void_p, c.c_int]
x.XCloseDisplay.argtypes = [c.c_void_p]
t.XTestFakeKeyEvent.argtypes = [c.c_void_p, c.c_uint, c.c_int, c.c_ulong]
t.XTestFakeButtonEvent.argtypes = [c.c_void_p, c.c_uint, c.c_int, c.c_ulong]
t.XTestFakeMotionEvent.argtypes = [c.c_void_p, c.c_int, c.c_int, c.c_int, c.c_ulong]
d = x.XOpenDisplay(None)
if not d: raise RuntimeError('Run under Xvfb')
def key(name, down):
    t.XTestFakeKeyEvent(d, x.XKeysymToKeycode(d, x.XStringToKeysym(name.encode())), down, 0)
modifier = sys.argv[1]
if modifier != 'none': key(modifier, 1)
if sys.argv[2] == 'click':
    t.XTestFakeMotionEvent(d, -1, int(sys.argv[3]), int(sys.argv[4]), 0)
    t.XTestFakeButtonEvent(d, 1, 1, 0)
    t.XTestFakeButtonEvent(d, 1, 0, 0)
elif sys.argv[2] == 'drag':
    t.XTestFakeMotionEvent(d, -1, int(sys.argv[3]), int(sys.argv[4]), 0)
    t.XTestFakeButtonEvent(d, 1, 1, 0)
    t.XTestFakeMotionEvent(d, -1, int(sys.argv[5]), int(sys.argv[6]), 100)
    t.XTestFakeButtonEvent(d, 1, 0, 100)
else:
    key(sys.argv[2], 1)
    key(sys.argv[2], 0)
if modifier != 'none': key(modifier, 0)
x.XSync(d, 0)
x.XCloseDisplay(d)
