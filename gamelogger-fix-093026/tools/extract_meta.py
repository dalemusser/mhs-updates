import gzip, struct, sys
src, out = sys.argv[1], sys.argv[2]
data = gzip.open(src).read()
assert data[:16] == b'UnityWebData1.0\x00', data[:16]
hdr_end = struct.unpack('<I', data[16:20])[0]
pos = 20; files = {}
while pos < hdr_end:
    off, size, nlen = struct.unpack('<III', data[pos:pos+12]); pos += 12
    name = data[pos:pos+nlen].decode(); pos += nlen
    files[name] = (off, size)
m = [n for n in files if n.endswith('global-metadata.dat')][0]
off, size = files[m]
open(out, 'wb').write(data[off:off+size])
print(src, 'files', len(files), m, size)
